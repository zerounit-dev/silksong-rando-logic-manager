using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;
using static Silksong_Rando_Logic_Manager.Services.RoomPresentationRules;

namespace SilksongRandoLogicManager.Client.RoomEditing;

/// <summary>Applied-value guidance only. Resolver IDs and durable parse status remain incoming facts.</summary>
public static class RoomTableDiagnostics
{
    public static void Apply(RoomCompleteDocument document, RoomDiagnosticIndex.Facts global,
        IReadOnlyList<RoomSubroomRow> subrooms, IReadOnlyList<RoomTransitionRow> transitions,
        IReadOnlyList<RoomConnectionRow> connections, IReadOnlyList<RoomCheckRow> checks)
    {
        var roomId = document.Observation.RoomId;
        var archived = document.Header.Observation.IsArchived;
        var subs = subrooms.Where(x => x.EntityId is not null).ToArray();
        var activeSubs = subs.Where(x => !x.IsArchived).ToArray();
        var exits = transitions.Where(x => x.EntityId is not null && !x.IsArchived).ToArray();
        var paths = connections.Where(x => x.EntityId is not null && !x.IsArchived).ToArray();
        // Invocation-local prepared facts preserve pairwise mixed-ID rules.
        var savedTransitions = document.Transitions.ToDictionary(x => x.Observation.EntityId);
        var savedConnections = document.Connections.ToDictionary(x => x.Observation.EntityId);
        var savedChecks = document.Checks.ToDictionary(x => x.Observation.EntityId);
        var endpoints = new TransitionEndpointCounts();
        foreach (var row in exits)
        {
            var saved = savedTransitions.GetValueOrDefault(row.EntityId!.Value);
            endpoints.Add(row.Values.DestinationRoomReferenceText, saved?.ResolvedDestinationRoomId,
                row.Values.DestinationTransitionAliasText, saved?.ResolvedDestinationTransitionId);
        }
        var connectionFacts = connections.ToDictionary(row => row, Fact);
        var pathways = new PreparedPathways(paths.Select(row => connectionFacts[row]));
        var canonicalNames = checks.ToDictionary(row => row, row => CanonicalSegment(row.Values.FriendlyName) ?? (Text(row.Values.FriendlyName) ? Key(row.Values.FriendlyName) : ""));
        var canonicalCounts = checks.Where(x => !x.IsArchived && x.EntityId is not null).GroupBy(x => canonicalNames[x]).ToDictionary(x => x.Key, x => x.Count());
        var subroomReferences = ReferenceIndex(subs.Select(x => (x.EntityId!.Value, x.Values.ReferenceId, x.IsArchived)));
        var roomReferences = global.References;
        var subroomNames = Counts(activeSubs.Select(x => x.Values.FriendlyName));
        var subroomKeys = Counts(activeSubs.Select(x => x.Values.ReferenceId));
        var exitAliases = Counts(exits.Select(x => x.Values.Alias));
        var exitNames = Counts(exits.Select(x => x.Values.FriendlyName));
        var uniqueRoomReference = MatchCount(document.Header.Values.ReferenceId, roomReferences) == 1;
        var canonicalConnections = paths.Where(x => Text(x.Values.Alias))
            .GroupBy(x => x.Values.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            // The workspace sequence already replays immutable manual placement,
            // including accepted reorders/creations not yet in the saved document.
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var destinations = global.Destinations;
        var sceneFrames = document.Scene?.Frames.Select(x => (Guid?)x.EntityId).ToHashSet();
        var sceneMarkers = document.Scene?.Markers.Select(x => ((Guid?)x.EntityId, x.Kind)).ToHashSet();
        var used = document.Transitions.Where(x => !x.Observation.IsArchived).Select(x => x.ResolvedSourceSubroomId)
            .Concat(document.Connections.Where(x => !x.Observation.IsArchived).SelectMany(x => new[] { x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId }))
            .Concat(document.Checks.Where(x => !x.Observation.IsArchived).Select(x => x.ResolvedSubroomId)).OfType<Guid>().ToHashSet();
        foreach (var row in subrooms)
        {
            Set(row, ("name", DuplicateSeverity(row.Values.FriendlyName, subroomNames)),
                ("reference", Max(DuplicateSeverity(row.Values.ReferenceId, subroomKeys), row.EntityId is { } id && !used.Contains(id) ? V2Severity.Warning : V2Severity.Neutral)));
            Target(row, RowTargetCoordinatePresentation.Frame(row.Geometry.X, row.Geometry.Y, row.Geometry.Width, row.Geometry.Height), row.Geometry.EnableAnnotation, false);
        }
        foreach (var row in transitions)
        {
            var saved = row.EntityId is { } id ? savedTransitions.GetValueOrDefault(id) : null;
            var source = IndexedReference(row.Values.SourceSubroomReferenceText, saved?.ResolvedSourceSubroomId, subroomReferences);
            var room = IndexedReference(row.Values.DestinationRoomReferenceText, saved?.ResolvedDestinationRoomId, roomReferences);
            var target = saved?.ResolvedDestinationRoomId is { } targetId ? destinations.GetValueOrDefault(targetId) : null;
            var alias = !Text(row.Values.DestinationTransitionAliasText) ? V2ReferenceState.None
                : room == V2ReferenceState.TargetArchived ? V2ReferenceState.TargetArchived
                : room != V2ReferenceState.Resolved ? saved?.ResolvedDestinationTransitionId is null ? V2ReferenceState.Unresolved : V2ReferenceState.OutOfSync
                 : IndexedReference(row.Values.DestinationTransitionAliasText, saved?.ResolvedDestinationTransitionId, target?.References);
            var pair = Text(row.Values.DestinationRoomReferenceText) && Text(row.Values.DestinationTransitionAliasText)
                && endpoints.Count(row.Values.DestinationRoomReferenceText, saved?.ResolvedDestinationRoomId,
                    row.Values.DestinationTransitionAliasText, saved?.ResolvedDestinationTransitionId) > 1 ? V2Severity.Danger : V2Severity.Neutral;
            Set(row, ("alias", Max(RoomAuthoredAlias.Severity(row.Values.Alias), DuplicateSeverity(row.Values.Alias, exitAliases))), ("name", DuplicateSeverity(row.Values.FriendlyName, exitNames)),
                ("source", ReferenceSeverity(Presence(row.Values.SourceSubroomReferenceText, activeSubs.Length > 0), source)),
                ("destination-room", ReferenceSeverity(Required(row.Values.DestinationRoomReferenceText), room)),
                ("destination-alias", ReferenceSeverity(Max(Alias(row.Values.DestinationTransitionAliasText, []), pair), alias)),
                ("requirements", Requirement(row.Values.Requirements, row.Values.IsTodo, saved?.Values.Requirements, saved?.RequirementsParseSucceeded)));
            Titles(row, ("source", source), ("destination-room", room), ("destination-alias", alias));
            AuthoredAliasTitle(row, row.Values.Alias);
            row.DestinationAliases = target?.Suggestions ?? [];
            var inverseCount = saved?.ResolvedDestinationRoomId is null || saved.ResolvedDestinationTransitionId is null ? 0
                : target?.Inverses.GetValueOrDefault(((Guid?)roomId, row.EntityId)) ?? 0;
            var resolved = saved?.ResolvedDestinationTransitionId is { } transitionId ? target?.ById.GetValueOrDefault(transitionId) : null;
            var hasDestination = saved?.ResolvedDestinationRoomId is not null && saved.ResolvedDestinationTransitionId is not null;
            row.InverseIcon = !hasDestination ? "" : inverseCount == 1 ? "fa-circle-check" : inverseCount > 1 ? "fa-circle-exclamation" : "fa-triangle-exclamation";
            row.InverseClass = !hasDestination || inverseCount == 1 ? "" : inverseCount > 1 ? "danger" : "warning";
            row.InverseTitle = inverseCount == 1 ? "One inverse transition found; open inverse room" : inverseCount > 1 ? "Multiple inverse transitions found" : "No inverse transition found";
            row.InverseRoomId = inverseCount == 1 ? saved!.ResolvedDestinationRoomId : null;
            row.CanPrepareInverse = !row.IsArchived && resolved is not null && (!Text(resolved.Values.DestinationRoomReferenceText) || !Text(resolved.Values.DestinationTransitionAliasText))
                && uniqueRoomReference && MatchCount(row.Values.Alias, exitAliases) == 1;
            Target(row, RowTargetCoordinatePresentation.ComparedPoint(row.Metadata.AnnotationSceneUnitX, row.Metadata.AnnotationSceneUnitY, row.Metadata.InGamePositionX, row.Metadata.InGamePositionY), row.EnableAnnotation, true);
        }
        foreach (var row in connections)
        {
            var saved = row.EntityId is { } id ? savedConnections.GetValueOrDefault(id) : null;
            row.AttentionSourceSubroomId = row.IsArchived ? null : saved?.ResolvedSourceSubroomId;
            row.AttentionDestinationSubroomId = row.IsArchived ? null : saved?.ResolvedDestinationSubroomId;
            var source = IndexedReference(row.Values.SourceSubroomReferenceText, saved?.ResolvedSourceSubroomId, subroomReferences);
            var destination = IndexedReference(row.Values.DestinationSubroomReferenceText, saved?.ResolvedDestinationSubroomId, subroomReferences);
            var pathway = pathways.Classify(connectionFacts[row]);
            var state = pathway.State;
            var severity = Max(pathway.Severity, activeSubs.Length == 0 || saved?.ResolvedSourceSubroomId is { } sid && sid == saved.ResolvedDestinationSubroomId ? V2Severity.Danger : V2Severity.Neutral);
            Set(row, ("alias", RoomAuthoredAlias.Severity(row.Values.Alias)), ("name", Required(row.Values.FriendlyName)),
                ("source", ReferenceSeverity(Presence(row.Values.SourceSubroomReferenceText, activeSubs.Length > 0), source)),
                ("destination", ReferenceSeverity(Presence(row.Values.DestinationSubroomReferenceText, activeSubs.Length > 0), destination)),
                ("requirements", Requirement(row.Values.Requirements, row.Values.IsTodo, saved?.Values.Requirements, saved?.RequirementsParseSucceeded)));
            Titles(row, ("source", source), ("destination", destination));
            AuthoredAliasTitle(row, row.Values.Alias);
            row.PathwayClass = Css(row.IsArchived || row.EntityId is null ? V2Severity.Neutral : severity);
            row.PathwayTitle = state;
            row.PathwayIcon = state switch { "one-way" => "fa-arrow-right connection-state-one-way", "bidirectional" => "fa-arrow-right-arrow-left", "ambiguous reverse match" => "fa-circle-exclamation", _ => "fa-triangle-exclamation" };
            row.CanScaffoldInverse = !row.IsArchived && severity == V2Severity.Neutral && state == "one-way" && saved?.ResolvedSourceSubroomId is not null && saved.ResolvedDestinationSubroomId is not null;
            var eligible = !row.IsArchived && Alias(row.Values.Alias, []) == V2Severity.Neutral && Required(row.Values.FriendlyName) == V2Severity.Neutral && severity == V2Severity.Neutral;
            var canonical = Text(row.Values.Alias) ? canonicalConnections.GetValueOrDefault(row.Values.Alias.Trim()) ?? row : row;
            row.AnnotationMarkerId = eligible ? canonical.EntityId : null;
            Target(row, RowTargetCoordinatePresentation.Point(canonical.X, canonical.Y), canonical.EnableAnnotation, false, eligible);
        }
        foreach (var row in checks)
        {
            var saved = row.EntityId is { } id ? savedChecks.GetValueOrDefault(id) : null;
            var name = row.Values.FriendlyName;
            var canonical = canonicalNames[row];
            var duplicate = !archived && Text(name) && canonicalCounts.GetValueOrDefault(canonical) > 1;
            var outside = Text(name) && global.CheckOwners.TryGetValue(name.Trim(), out var owners) && (owners.Count > 1 || !owners.Contains(roomId));
            var subroom = IndexedReference(row.Values.SubroomReferenceText, saved?.ResolvedSubroomId, subroomReferences);
            Set(row, ("name", duplicate ? V2Severity.Danger : !Text(name) || outside ? V2Severity.Warning : V2Severity.Neutral),
                ("subroom", ReferenceSeverity(Presence(row.Values.SubroomReferenceText, activeSubs.Length > 0), subroom)),
                ("requirements", Requirement(row.Values.Requirements, row.Values.IsTodo, saved?.Values.Requirements, saved?.RequirementsParseSucceeded)),
                ("type", !archived && !CheckLocationTypeCatalogue.Definitions.Any(x => x.OutputValue == row.Values.LocationType) ? V2Severity.Danger : V2Severity.Neutral));
            Titles(row, ("subroom", subroom));
            Target(row, RowTargetCoordinatePresentation.ComparedPoint(row.Metadata.AnnotationSceneUnitX, row.Metadata.AnnotationSceneUnitY, row.Metadata.InGamePositionX, row.Metadata.InGamePositionY), row.EnableAnnotation, true);
        }

        V2Severity Requirement(string text, bool todo, string? original, bool? parsed) => text != original || archived
            ? !Text(text) || todo ? V2Severity.Warning : V2Severity.Neutral : Max(Requirements(text, parsed), todo ? V2Severity.Warning : V2Severity.Neutral);
        PathwayFact Fact(RoomConnectionRow row)
        {
            var saved = row.EntityId is { } id ? savedConnections.GetValueOrDefault(id) : null;
            return new(row.EntityId ?? row.ClientId, row.Values.Alias, row.Values.FriendlyName,
                row.Values.SourceSubroomReferenceText, saved?.ResolvedSourceSubroomId, row.Values.DestinationSubroomReferenceText, saved?.ResolvedDestinationSubroomId, row.IsArchived);
        }
        void Target(RoomTableRow row, RowTargetCoordinateState state, bool enabled, bool game, bool eligible = true)
        {
            var kind = row is RoomSubroomRow ? "subroom" : row is RoomTransitionRow ? "exit" : row is RoomConnectionRow ? "connection" : "check";
            var marker = row is RoomConnectionRow c ? c.AnnotationMarkerId : row.EntityId;
            var rendered = row is RoomSubroomRow ? sceneFrames?.Contains(row.EntityId) == true : sceneMarkers?.Contains((marker, kind)) == true;
            row.CanAnnotate = row.EntityId is not null && !row.IsArchived && !archived && document.Scene?.HasValidBounds == true && eligible;
            row.AnnotationAction = rendered ? "select" : state != RowTargetCoordinateState.AnnotationUnavailable && !enabled ? "show" : "place";
            var action = rendered ? "select annotation" : row.AnnotationAction == "show" ? "show and select annotation" : eligible ? "re-arm placement" : "Connection annotation is unavailable because this connection group is ineligible.";
            if (eligible && document.Scene?.HasValidBounds != true)
                action = "Annotation action is unavailable because room bounds are absent or invalid.";
            row.AnnotationClass = RowTargetCoordinatePresentation.CssClass(state);
            row.AnnotationTitle = RowTargetCoordinatePresentation.Tooltip(action, state, enabled, game);
        }
    }
    // Same requires nonblank text and compares trimmed values with OrdinalIgnoreCase.
    // Do not use Key here: canonical-name fallback has its own existing normalization.
    private static Dictionary<string, int> Counts(IEnumerable<string?> values) => values.Where(Text)
        .GroupBy(x => x!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
    private static int MatchCount(string? text, IReadOnlyDictionary<string, int> counts) => Text(text) ? counts.GetValueOrDefault(text!.Trim()) : 0;
    private static V2Severity DuplicateSeverity(string? text, IReadOnlyDictionary<string, int> counts) => MatchCount(text, counts) > 1 ? V2Severity.Danger : Required(text);
    private static Dictionary<string, (int Active, Guid Id, bool Archived)> ReferenceIndex(IEnumerable<(Guid Id, string Text, bool Archived)> candidates) => candidates
        .Where(x => Text(x.Text)).GroupBy(x => x.Text.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x =>
        {
            var active = x.Where(candidate => !candidate.Archived).ToArray();
            return (active.Length, active.Length == 1 ? active[0].Id : Guid.Empty, x.Any(candidate => candidate.Archived));
        }, StringComparer.OrdinalIgnoreCase);
    private static int MatchCount(string? text, IReadOnlyDictionary<string, (int Active, Guid Id, bool Archived)> counts) => Text(text) ? counts.GetValueOrDefault(text!.Trim()).Active : 0;
    private static V2ReferenceState IndexedReference(string? text, Guid? id, IReadOnlyDictionary<string, (int Active, Guid Id, bool Archived)>? candidates)
    {
        if (!Text(text)) return V2ReferenceState.None;
        var match = candidates?.GetValueOrDefault(text!.Trim()) ?? default;
        if (match.Active == 1) return match.Id == id ? V2ReferenceState.Resolved : V2ReferenceState.OutOfSync;
        if (match.Active > 1) return V2ReferenceState.Ambiguous;
        if (match.Archived) return V2ReferenceState.TargetArchived;
        return id is null ? V2ReferenceState.Unresolved : V2ReferenceState.OutOfSync;
    }
    private static string Css(V2Severity value) => value switch { V2Severity.Warning => "warning", V2Severity.Danger => "danger", _ => "" };
    private static void Set(RoomTableRow row, params (string Field, V2Severity Severity)[] values)
    {
        row.FieldClasses = values.ToImmutableDictionary(x => x.Field, x => row.IsArchived || row.EntityId is null ? "" : Css(x.Severity));
        var controls = row switch
        {
            RoomTransitionRow x => (x.Values.IsTodo, x.Values.IsVerified), RoomConnectionRow x => (x.Values.IsTodo, x.Values.IsVerified),
            RoomCheckRow x => (x.Values.IsTodo, x.Values.IsVerified), _ => (false, (bool?)null)
        };
        row.FieldClasses = row.FieldClasses.SetItem("todo", !row.IsArchived && controls.Item1 ? "todo" : "")
            .SetItem("verification", row.IsArchived || row.EntityId is null ? "" : controls.Item2 == true ? "verification-success" : controls.Item2 == false ? "verification-danger" : "verification-warning");
    }
    private static void Titles(RoomTableRow row, params (string Field, V2ReferenceState State)[] values) => row.FieldTitles = values.ToImmutableDictionary(x => x.Field, x => x.State.ToString());
    private static void AuthoredAliasTitle(RoomTableRow row, string value)
    {
        if (row.FieldClass("alias") != "danger") return;
        var hyphen = RoomAuthoredAlias.HasHyphen(value);
        var padding = RoomAuthoredAlias.HasOuterWhitespace(value);
        var internalWhitespace = RoomAuthoredAlias.HasInternalWhitespace(value);
        if (!hyphen && !padding && !internalWhitespace) return;
        var title = hyphen ? "Hyphens are not allowed in aliases." : "";
        if (padding) title += (hyphen ? " " : "") + "Edit Alias to remove the leading or trailing whitespace.";
        if (internalWhitespace) title += (hyphen || padding ? " " : "") + "Internal whitespace is not allowed in aliases.";
        row.FieldTitles = row.FieldTitles.SetItem("alias", title);
    }
}
