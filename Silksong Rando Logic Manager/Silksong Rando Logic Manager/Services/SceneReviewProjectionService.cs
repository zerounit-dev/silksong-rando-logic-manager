using System.Globalization;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneReviewProjectionService
{
    public SceneReviewSpatialProjection Project(IEnumerable<SceneDumpObject> nodes, IReadOnlySet<Guid> visibleObjectIds, Guid? hoveredObjectId)
    {
        return Project(CreatePoints(nodes), visibleObjectIds, hoveredObjectId);
    }

    public IReadOnlyList<SceneReviewPoint> CreatePoints(IEnumerable<SceneDumpObject> nodes) => Flatten(nodes)
            .Select(node => (node.Id, node.Classification, Position: Position(node)))
            .Where(item => item.Position is not null)
            .Select(item => new SceneReviewPoint(item.Id, item.Position!.Value.X, -item.Position.Value.Y, item.Classification))
            .ToArray();

    public SceneReviewSpatialProjection Project(IReadOnlyList<SceneReviewPoint> allPoints, IReadOnlySet<Guid> visibleObjectIds, Guid? hoveredObjectId)
    {
        var points = allPoints.Where(point => visibleObjectIds.Contains(point.Id)).ToArray();
        var hoveredPoint = allPoints.SingleOrDefault(point => point.Id == hoveredObjectId);
        var bounds = Bounds(hoveredPoint is null ? points : points.Append(hoveredPoint).ToArray());
        return new SceneReviewSpatialProjection(points, hoveredPoint, bounds);
    }

    public IReadOnlyDictionary<string, int> CountCheckIdentities(IEnumerable<SceneDumpObject> nodes) => nodes
        .Where(node => node.Classification is SceneDumpClassification.Check or SceneDumpClassification.Enemy)
        .Select(SceneDumpIdentity.GameId).Where(id => !string.IsNullOrWhiteSpace(id))
        .GroupBy(id => id!.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public SceneReviewImportDraft CreateDraft(SceneDumpObject node, SceneImportPreview preview, Guid? targetRoomId,
        IReadOnlyDictionary<string, int> checkIdentityCounts)
    {
        var targetMatches = SceneImportService.MatchObject(node, preview.Transitions, preview.Checks).Matches
            .Where(match => match.RoomId == targetRoomId && match.Classification ==
                (node.Classification == SceneDumpClassification.Exit ? SceneDumpClassification.Exit : SceneDumpClassification.Check))
            .ToArray();
        var gameId = SceneDumpIdentity.GameId(node);
        var uniqueIncoming = node.Classification == SceneDumpClassification.Exit ||
            gameId is not null && checkIdentityCounts.TryGetValue(gameId.Trim(), out var count) && count == 1;
        Guid? updateId = uniqueIncoming && targetMatches.Length == 1 ? targetMatches[0].RecordId : null;
        var name = updateId is { } id ? SelectableRecords(preview, node.Classification, targetRoomId).Single(record => record.Id == id).FriendlyName : node.Name;
        return new SceneReviewImportDraft(node.Id, updateId is null ? SceneImportMode.Create : SceneImportMode.Update, updateId, name);
    }

    public IReadOnlyList<SceneImportSelectableRecord> SelectableRecords(SceneImportPreview preview, SceneDumpClassification classification, Guid? targetRoomId) =>
        (classification == SceneDumpClassification.Exit ? preview.Transitions : preview.Checks)
            .Where(record => record.RoomId == targetRoomId)
            .ToArray();

    public IReadOnlyList<SceneImportSelectableRecord> WorkbenchRecords(SceneImportPreview preview, SceneDumpClassification classification, Guid? roomId) =>
        SelectableRecords(preview, classification, roomId).Where(record => classification == SceneDumpClassification.Exit ||
            record.IsEnemy == (classification == SceneDumpClassification.Enemy)).ToArray();

    public IReadOnlyDictionary<Guid, SceneReviewImportDraft> WorkbenchDrafts(IEnumerable<SceneDumpObject> source,
        SceneImportPreview preview, Guid? roomId, IReadOnlyDictionary<Guid, SceneReviewImportDraft>? deliberateChoices = null)
    {
        var nodes = source.Where(node => SceneDumpIdentity.IsImportable(node.Classification)).ToArray();
        var counts = CountCheckIdentities(nodes);
        var result = new Dictionary<Guid, SceneReviewImportDraft>();
        var exactByObject = nodes.ToDictionary(node => node.Id, node => WorkbenchRecords(preview, node.Classification, roomId)
            .Where(record => SameId(record.InGameId, SceneDumpIdentity.GameId(node))).ToArray());
        var reserved = new HashSet<Guid>();
        var automatic = new List<SceneDumpObject>();
        foreach (var node in nodes)
        {
            var records = WorkbenchRecords(preview, node.Classification, roomId);
            if (deliberateChoices?.TryGetValue(node.Id, out var deliberate) == true && deliberate.HasDeliberateRecordSelection &&
                (deliberate.Mode == SceneImportMode.Create && deliberate.ExistingRecordId is null ||
                 deliberate.Mode == SceneImportMode.Update && records.Any(record => record.Id == deliberate.ExistingRecordId)))
            {
                result[node.Id] = deliberate;
                if (deliberate.ExistingRecordId is { } manualTarget) reserved.Add(manualTarget);
                continue;
            }
            automatic.Add(node);
        }
        // Exact identity claims have precedence, including blocked claims: cohorts may not borrow their targets.
        var exactClaims = automatic.SelectMany(node => exactByObject[node.Id].Select(record => (node.Id, Target: record.Id)))
            .GroupBy(claim => claim.Target).ToDictionary(group => group.Key, group => group.Count());
        foreach (var node in automatic)
        {
            var records = WorkbenchRecords(preview, node.Classification, roomId);
            var exact = exactByObject[node.Id];
            var uniqueIncoming = node.Classification == SceneDumpClassification.Exit ||
                counts.GetValueOrDefault(SceneDumpIdentity.GameId(node)?.Trim() ?? string.Empty) == 1;
            Guid? target = exact.Length == 1 && uniqueIncoming && (node.Classification != SceneDumpClassification.Enemy ||
                !reserved.Contains(exact[0].Id) && exactClaims[exact[0].Id] == 1) ? exact[0].Id : null;
            var reason = target is not null ? "Unique exact game ID" : string.Empty;
            var unresolved = node.Classification == SceneDumpClassification.Check && target is null;
            if (unresolved)
                reason = exact.Length > 0 || !uniqueIncoming
                    ? "Unresolved — ambiguous or competing exact identity claims"
                    : "Unresolved — no unique eligible exact game ID match; choose an existing target or Create new";
            if (node.Classification == SceneDumpClassification.Enemy && target is null)
            {
                var suggestions = records.Where(record => SameSpecies(node, record, preview) || EqualXY(node, record) || exact.Contains(record)).ToArray();
                if (exact.Length > 0 || counts.GetValueOrDefault(SceneDumpIdentity.GameId(node)?.Trim() ?? string.Empty) > 1)
                {
                    unresolved = true;
                    reason = "Unresolved — ambiguous or competing exact identity claims";
                }
                else if (suggestions.Length > 0)
                {
                    unresolved = true;
                    reason = "Unresolved — " + string.Join("; ", suggestions.Select(record => $"{record.FriendlyName}: " +
                        (exact.Contains(record) ? "game ID" : SameSpecies(node, record, preview) ? "species/name" : "world XY") +
                        (WorldConflict(node, record) ? ", known world conflict" : string.Empty)));
                }
            }
            var current = records.SingleOrDefault(record => record.Id == target);
            result[node.Id] = new(node.Id, target is null ? SceneImportMode.Create : SceneImportMode.Update, target, current?.FriendlyName ?? node.Name)
            { Unresolved = unresolved, MatchReason = reason, Requirements = current?.Requirements ?? SceneEnemySpawnNotesResolver.EnemyRequirements(node),
                Notes = current?.Notes ?? SceneEnemySpawnNotesResolver.EnemyNotes(node) };
        }
        foreach (var claim in automatic.Where(node => node.Classification != SceneDumpClassification.Enemy)
            .Select(node => result[node.Id]).Where(draft => draft.ExistingRecordId is not null)
            .GroupBy(draft => draft.ExistingRecordId).Where(group => group.Count() > 1))
            foreach (var draft in claim) result[draft.ObjectId] = draft with
            { Mode = SceneImportMode.Create, ExistingRecordId = null, Unresolved = true, MatchReason = "Unresolved — competing automatic claims",
                Requirements = SceneEnemySpawnNotesResolver.EnemyRequirements(nodes.Single(node => node.Id == draft.ObjectId)),
                Notes = SceneEnemySpawnNotesResolver.EnemyNotes(nodes.Single(node => node.Id == draft.ObjectId)) };
        reserved.UnionWith(exactClaims.Keys);
        var remaining = automatic.Where(node => node.Classification == SceneDumpClassification.Enemy &&
            exactByObject[node.Id].Length == 0).ToArray();
        var edges = remaining.ToDictionary(node => node.Id, node => WorkbenchRecords(preview, node.Classification, roomId)
            .Where(record => !reserved.Contains(record.Id) && SameSpecies(node, record, preview) && EqualXY(node, record) && !WorldConflict(node, record)).ToArray());
        // Inspect the entire residual graph, never shrink it by Apply, visibility, or earlier composite allocations.
        var handled = new HashSet<Guid>();
        foreach (var node in remaining)
        {
            if (!handled.Add(node.Id)) continue;
            var candidates = edges[node.Id];
            if (candidates.Length == 0) continue;
            var ids = candidates.Select(record => record.Id).ToHashSet();
            var members = remaining.Where(peer => ids.SetEquals(edges[peer.Id].Select(record => record.Id))).ToArray();
            if (members.Any(peer => counts.GetValueOrDefault(SceneDumpIdentity.GameId(peer)?.Trim() ?? string.Empty) > 1)) continue;
            if (candidates.Length == 1)
            {
                // Retain the original single-candidate reverse-claim guard.
                if (members.Length == 1) Select(node, candidates[0], "Compatible species/name and equal imported world XY; no known world conflict");
                else foreach (var peer in members) result[peer.Id] = result[peer.Id] with { Unresolved = true, MatchReason = "Unresolved — competing automatic claims" };
                continue;
            }
            handled.UnionWith(members.Select(peer => peer.Id));
            if (members.Length != candidates.Length || remaining.Any(peer => !members.Contains(peer) && edges[peer.Id].Any(record => ids.Contains(record.Id))) ||
                !candidates.All(record => UniformEnemyTargets(candidates[0], record))) continue;
            var numbered = candidates.Select(record => (Record: record, Number: EnemyFriendlyNumber(record.FriendlyName))).ToArray();
            if (numbered.Any(item => item.Number is null) || numbered.Select(item => item.Number!.Value.Number).Distinct().Count() != candidates.Length ||
                numbered.Select(item => item.Number!.Value.Base).Distinct(StringComparer.Ordinal).Count() != 1) continue;
            var ordered = numbered.OrderBy(item => item.Number!.Value.Number).ToArray();
            for (var rank = 0; rank < members.Length; rank++)
                Select(members[rank], ordered[rank].Record, "Numbered one-to-one pairing of interchangeable Enemy group; capture order to FriendlyName number order");
        }
        return result;

        void Select(SceneDumpObject node, SceneImportSelectableRecord record, string reason) => result[node.Id] = result[node.Id] with
        { Mode = SceneImportMode.Update, ExistingRecordId = record.Id, FriendlyName = record.FriendlyName,
            Unresolved = false, MatchReason = reason, Requirements = record.Requirements, Notes = record.Notes };
    }

    private static bool UniformEnemyTargets(SceneImportSelectableRecord a, SceneImportSelectableRecord b) =>
        a.Requirements == b.Requirements && a.Notes == b.Notes && a.LocationType == b.LocationType &&
        a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.EnemyEquivalence is not null && a.EnemyEquivalence == b.EnemyEquivalence;

    private static (string Base, int Number)? EnemyFriendlyNumber(string name)
    {
        var current = name.Trim();
        var split = current.LastIndexOf(' ');
        if (split < 1) return null;
        var suffix = current[(split + 1)..];
        if (suffix.Length == 0 || suffix.Any(character => character is < '0' or > '9') ||
            !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        var basis = RequirementCatalogueLanguage.NormalizeIdentification(current[..split]);
        return basis.Length == 0 ? null : (basis, number);
    }

    private static bool SameId(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool EqualXY(SceneDumpObject node, SceneImportSelectableRecord record) =>
        node.WorldPosition is { X: { } x, Y: { } y } && record.X is { } a && record.Y is { } b &&
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(a) && double.IsFinite(b) && x == a && y == b;
    private static bool SameSpecies(SceneDumpObject node, SceneImportSelectableRecord record, SceneImportPreview preview)
    {
        var names = node.JournalName is { } journal ? preview.EnemyNames.Where(mapping =>
            RequirementCatalogueLanguage.NormalizeIdentification(mapping.OutputValue) == RequirementCatalogueLanguage.NormalizeIdentification(journal)).ToArray() : [];
        var basis = names.Length == 1 ? names[0].Name : node.JournalName ?? EnemyFallbackComparisonName(node.Name);
        // Authored names are weak evidence, never sufficient alone. Strip only a numeric friendly-name suffix, never game IDs.
        var current = record.FriendlyName.Trim();
        var split = current.LastIndexOf(' ');
        if (split >= 0 && int.TryParse(current[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) current = current[..split];
        var key = RequirementCatalogueLanguage.NormalizeIdentification(basis);
        return key.Length > 0 && key == RequirementCatalogueLanguage.NormalizeIdentification(current);
    }
    private static string EnemyFallbackComparisonName(string name)
    {
        // Only transient weak-name evidence loses one terminal " (ASCII digits)".
        // Journal/canonical names, generated names and opaque IDs never use this helper.
        var trimmed = name.Trim();
        if (!trimmed.EndsWith(')')) return trimmed;
        var index = trimmed.Length - 2;
        var lastDigit = index;
        while (index >= 0 && trimmed[index] is >= '0' and <= '9') index--;
        if (index == lastDigit || index < 1 || trimmed[index] != '(' || trimmed[index - 1] != ' ') return trimmed;
        var basis = trimmed[..(index - 1)].TrimEnd();
        return basis.Length > 0 ? basis : trimmed;
    }
    private static bool WorldConflict(SceneDumpObject node, SceneImportSelectableRecord record) =>
        node.PlacementWorld == ScenePlacementWorldAvailability.BlackThreadOnly && record.Requirements.Trim() == "Normal World Spawn" ||
        node.PlacementWorld == ScenePlacementWorldAvailability.NormalOnly && record.Requirements.Trim() == "Black Thread World Spawn";

    public SceneReviewSpatialProjection ProjectWorkbench(IReadOnlyList<SceneReviewPoint> shownPoints, SceneReviewBounds? frame, bool fitVisibleDots)
    {
        var extent = frame is null || fitVisibleDots ? shownPoints.ToList() : [];
        if (frame is not null)
        {
            extent.Add(new(Guid.Empty, frame.X, frame.Y, SceneDumpClassification.Other));
            extent.Add(new(Guid.Empty, frame.X + frame.Width, frame.Y + frame.Height, SceneDumpClassification.Other));
        }
        return new(shownPoints, null, Bounds(extent));
    }

    public void AllocateNames(IEnumerable<SceneDumpObject> sourceOrder, SceneImportPreview preview, Guid? targetRoomId,
        IDictionary<Guid, SceneReviewImportDraft> drafts)
    {
        var nodes = sourceOrder.ToArray();
        var canonicalNames = preview.EnemyNames
            .GroupBy(mapping => RequirementCatalogueLanguage.NormalizeIdentification(mapping.OutputValue), StringComparer.Ordinal)
            .Where(group => group.Key.Length != 0 && group.Select(mapping => mapping.Id).Distinct().Count() == 1)
            .Select(group => (group.Key, Name: group.First().Name))
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .ToDictionary(mapping => mapping.Key, mapping => mapping.Name, StringComparer.Ordinal);
        var createIds = nodes.Where(node => drafts.TryGetValue(node.Id, out var draft) && draft.Mode == SceneImportMode.Create).Select(node => node.Id).ToHashSet();
        var reserved = new HashSet<string>(preview.Transitions.Concat(preview.Checks)
            .Where(record => record.RoomId == targetRoomId).Select(record => record.FriendlyName.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts.Values.Where(draft => createIds.Contains(draft.ObjectId) && draft.AuthoredName is not null))
            reserved.Add(draft.AuthoredName!.Trim());
        foreach (var node in nodes)
        {
            if (!drafts.TryGetValue(node.Id, out var draft) || draft.Mode != SceneImportMode.Create) continue;
            if (draft.AuthoredName is { } authored) { drafts[node.Id] = draft with { FriendlyName = authored }; continue; }
            var basis = node.JournalName ?? node.Name;
            var enemy = node.Classification == SceneDumpClassification.Enemy;
            if (enemy && node.JournalName is { } journal &&
                canonicalNames.TryGetValue(RequirementCatalogueLanguage.NormalizeIdentification(journal), out var canonical))
                basis = canonical;
            var name = enemy ? $"{basis} 1" : basis;
            for (var number = 2; !reserved.Add(name.Trim()); number++)
                name = enemy ? $"{basis} {number.ToString(CultureInfo.InvariantCulture)}" : $"{basis} {number}";
            drafts[node.Id] = draft with { FriendlyName = name };
        }
    }

    private static SceneReviewBounds Bounds(IReadOnlyList<SceneReviewPoint> points)
    {
        if (points.Count == 0) return new SceneReviewBounds(-1, -1, 2, 2);
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var width = Math.Max(maxX - minX, 1d);
        var height = Math.Max(maxY - minY, 1d);
        return new SceneReviewBounds(minX - width * .03, minY - height * .03, width * 1.06, height * 1.06);
    }

    private static (double X, double Y)? Position(SceneDumpObject node) =>
        node.WorldPosition is { X: { } x, Y: { } y } && double.IsFinite(x) && double.IsFinite(y) ? (x, y) : null;

    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
}

public sealed record SceneReviewSpatialProjection(IReadOnlyList<SceneReviewPoint> Points, SceneReviewPoint? HoveredPoint, SceneReviewBounds Bounds);
public sealed record SceneReviewPoint(Guid Id, double X, double Y, SceneDumpClassification Classification);
public sealed record SceneReviewBounds(double X, double Y, double Width, double Height);
public sealed record SceneReviewImportDraft(Guid ObjectId, SceneImportMode Mode, Guid? ExistingRecordId, string FriendlyName, string? AuthoredName = null, bool HasDeliberateRecordSelection = false)
{
    public bool Apply { get; init; } = true;
    public bool Unresolved { get; init; }
    public string MatchReason { get; init; } = string.Empty;
    public string Requirements { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}
