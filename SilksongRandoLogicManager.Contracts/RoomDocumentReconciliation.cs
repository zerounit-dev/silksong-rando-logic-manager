using System.Collections.Immutable;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

public sealed record RoomConfirmedState(RoomCompleteDocument? Document, bool ExternallyStale);

/// <summary>
/// Private preparation of a complete confirmed-room replacement. It never mutates
/// native fields, live drafts, the immutable queue or the global catalogue. The
/// workspace installs the prepared state and reprojects later intent separately.
/// </summary>
public static class RoomDocumentReconciliation
{
    public static RoomConfirmedState FromComplete(RoomCompleteDocument document)
    {
        Validate(document);
        return new(document, false);
    }

    public static RoomConfirmedState Apply(RoomConfirmedState state, RoomDocumentDelta delta)
    {
        var old = state.Document ?? throw Invalid("A deleted room cannot accept an editing delta.");
        // A complete-family order is authority for exactly its supplied snapshots,
        // never permission to trim old rows until a malformed order happens to fit.
        // Validate partial removals as well, before selecting either application path.
        ValidateProjection(delta);
        var roomId = old.Observation.RoomId;
        if (!RoomEditingIdentity.ValidObservation(delta.Observed, roomId)
            || delta.CoversFrom is { } coverage && !RoomEditingIdentity.ValidObservation(coverage, roomId)
            || delta.Removed is null || (delta.SceneConcerns & ~AllSceneConcerns) != 0)
            throw Invalid("Unusable room delta observation.");
        // A later complete recovery observation already includes this earlier
        // commit. Incorporate its receipt without reinstalling an obsolete delta.
        if (delta.Observed.CacheFlagUtc < old.Observation.CacheFlagUtc) return state;

        var header = delta.Header ?? old.Header;
        var subrooms = delta.SubroomOrder is not null ? delta.Subrooms
            : Replace(old.Subrooms, delta.Subrooms, delta.Removed.Subrooms, x => x.Observation.EntityId);
        var explanations = delta.ExplanationOrder is not null ? delta.Explanations
            : Replace(old.Explanations, delta.Explanations, delta.Removed.Explanations, x => x.Observation.EntityId);
        var transitions = delta.TransitionOrder is not null ? delta.Transitions
            : Replace(old.Transitions, delta.Transitions, delta.Removed.Transitions, x => x.Observation.EntityId);
        var connections = delta.ConnectionOrder is not null ? delta.Connections
            : Replace(old.Connections, delta.Connections, delta.Removed.Connections, x => x.Observation.EntityId);
        // Both location-type partitions are one complete family/atomic order pair.
        var checks = delta.CheckOrder is not null ? delta.Checks
            : Replace(old.Checks, delta.Checks, delta.Removed.Checks, x => x.Observation.EntityId);
        if (delta.SubroomOrder is { } so) subrooms = Order(subrooms, so, x => x.Observation);
        if (delta.ExplanationOrder is { } eo) explanations = Order(explanations, eo, x => x.Observation);
        if (delta.TransitionOrder is { } to) transitions = Order(transitions, to, x => x.Observation);
        if (delta.ConnectionOrder is { } co) connections = Order(connections, co, x => x.Observation);
        if (delta.CheckOrder is { } pair)
        {
            var locations = checks.Where(x => !CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)).ToImmutableArray();
            var enemies = checks.Where(x => CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)).ToImmutableArray();
            checks = Order(locations, pair.Checks, x => x.Observation)
                .AddRange(Order(enemies, pair.Enemies, x => x.Observation));
        }

        SceneLayoutView? scene = old.Scene;
        if (header.Observation.IsArchived) scene = null;
        else if (delta.SceneConcerns != RoomSceneConcerns.None)
        {
            var fresh = delta.Scene ?? throw Invalid("Missing named scene projection.");
            if (scene is null) scene = fresh;
            else
            {
                if (delta.SceneConcerns.HasFlag(RoomSceneConcerns.Canvas))
                    scene = scene with { Frames = fresh.Frames, Markers = fresh.Markers, Threads = fresh.Threads };
                if ((delta.SceneConcerns & (RoomSceneConcerns.Viewport | RoomSceneConcerns.BoundsImageAction)) != 0)
                    scene = scene with { HasValidBounds = fresh.HasValidBounds, SceneMinX = fresh.SceneMinX,
                        SceneMinY = fresh.SceneMinY, SceneMaxX = fresh.SceneMaxX, SceneMaxY = fresh.SceneMaxY };
                if (delta.SceneConcerns.HasFlag(RoomSceneConcerns.BoundsImageAction))
                {
                    scene = scene with { BoundsImageDiagnostic = fresh.BoundsImageDiagnostic };
                    if (scene.Image is { } retained && fresh.Image is { } observed)
                        scene = scene with { Image = retained with { BoundsImageSeverity = observed.BoundsImageSeverity } };
                }
                if (delta.SceneConcerns.HasFlag(RoomSceneConcerns.ImageDisplay)) scene = scene with { Image = fresh.Image };
            }
        }
        else if (delta.Scene is not null) throw Invalid("Unexpected unnamed scene projection.");

        var covers = delta.CoversFrom == old.Observation;
        var next = new RoomCompleteDocument(covers ? delta.Observed : old.Observation,
            header, subrooms, transitions, connections, checks, scene, explanations);
        Validate(next);
        return new(next, !covers);
    }

    private const RoomSceneConcerns AllSceneConcerns = RoomSceneConcerns.Canvas | RoomSceneConcerns.Viewport
        | RoomSceneConcerns.BoundsImageAction | RoomSceneConcerns.ImageDisplay;

    public static void Validate(RoomCompleteDocument document)
    {
        if (document is null || document.Header?.Observation is null || !document.Header.Observation.IsArchived && document.Scene is null)
            throw Invalid("Incomplete confirmed room document.");
        Validate(new RoomScalarSnapshot(document.Observation, document.Header, document.Subrooms,
            document.Transitions, document.Connections, document.Checks, document.Explanations));
    }

    public static void Validate(RoomScalarSnapshot document)
    {
        if (document is null || document.Header is null || document.Header.Observation is null
            || !RoomEditingIdentity.ValidObservation(document.Observation, document.Header.Observation.EntityId)
            || !RoomEditingIdentity.ValidOperation(new SaveRoomHeader(Observed(document.Header.Values, document.Header.Observation), document.Header.Values), document.Observation.RoomId)
            || document.Header.SceneImage is null || document.Header.Metadata is null || !Enum.IsDefined(document.Header.CompletionStatus))
            throw Invalid("Incomplete confirmed room document.");
        ValidateRows(document.Observation.RoomId, document.Subrooms, document.Transitions, document.Connections, document.Checks, document.Explanations);
    }

    /// <summary>
    /// Validate a publishable affected-family snapshot before releasing the server's
    /// projection pause. A full order must describe exactly the rows read with it,
    /// not a smaller inventory that depends on undeclared client-side deletions.
    /// </summary>
    public static void ValidateProjection(RoomDocumentDelta delta)
    {
        if (delta is null || delta.Observed is null || delta.Removed is null
            || !RoomEditingIdentity.ValidObservation(delta.Observed, delta.Observed.RoomId)
            || delta.CoversFrom is { } coverage && !RoomEditingIdentity.ValidObservation(coverage, delta.Observed.RoomId)
            || delta.Header is { } header && (header.Observation is null
                || header.Observation.EntityId != delta.Observed.RoomId || header.SceneImage is null || header.Metadata is null || !Enum.IsDefined(header.CompletionStatus)
                || !RoomEditingIdentity.ValidOperation(new SaveRoomHeader(Observed(header.Values,
                    header.Observation), header.Values), delta.Observed.RoomId))
            || (delta.SceneConcerns & ~AllSceneConcerns) != 0
            || delta.SceneConcerns == RoomSceneConcerns.None && delta.Scene is not null
            || delta.SceneConcerns != RoomSceneConcerns.None && delta.Header?.Observation.IsArchived != true && delta.Scene is null)
            throw Invalid("Unusable affected room projection.");
        ValidateRows(delta.Observed.RoomId, delta.Subrooms, delta.Transitions, delta.Connections, delta.Checks, delta.Explanations);
        // Replace performs the same disjoint-removal validation used by incorporation.
        _ = Replace<RoomSubroomSnapshot>([], delta.Subrooms, delta.Removed.Subrooms, x => x.Observation.EntityId);
        _ = Replace<RoomExplanationSnapshot>([], delta.Explanations, delta.Removed.Explanations, x => x.Observation.EntityId);
        _ = Replace<RoomTransitionSnapshot>([], delta.Transitions, delta.Removed.Transitions, x => x.Observation.EntityId);
        _ = Replace<RoomConnectionSnapshot>([], delta.Connections, delta.Removed.Connections, x => x.Observation.EntityId);
        _ = Replace<RoomCheckSnapshot>([], delta.Checks, delta.Removed.Checks, x => x.Observation.EntityId);
        if (delta.SubroomOrder is { } so) _ = Order(delta.Subrooms, so, x => x.Observation);
        if (delta.ExplanationOrder is { } eo) _ = Order(delta.Explanations, eo, x => x.Observation);
        if (delta.TransitionOrder is { } to) _ = Order(delta.Transitions, to, x => x.Observation);
        if (delta.ConnectionOrder is { } co) _ = Order(delta.Connections, co, x => x.Observation);
        if (delta.CheckOrder is { } pair)
        {
            _ = Order(delta.Checks.Where(x => !CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)).ToImmutableArray(), pair.Checks, x => x.Observation);
            _ = Order(delta.Checks.Where(x => CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)).ToImmutableArray(), pair.Enemies, x => x.Observation);
        }
    }

    private static void ValidateRows(Guid roomId, ImmutableArray<RoomSubroomSnapshot> subrooms,
        ImmutableArray<RoomTransitionSnapshot> transitions, ImmutableArray<RoomConnectionSnapshot> connections,
         ImmutableArray<RoomCheckSnapshot> checks, ImmutableArray<RoomExplanationSnapshot> explanations)
    {
        Unique(subrooms, x => x.Observation?.EntityId ?? Guid.Empty);
        Unique(transitions, x => x.Observation?.EntityId ?? Guid.Empty);
        Unique(connections, x => x.Observation?.EntityId ?? Guid.Empty);
        Unique(checks, x => x.Observation?.EntityId ?? Guid.Empty);
        Unique(explanations, x => x.Observation?.EntityId ?? Guid.Empty);
        foreach (var row in explanations)
            if (!RoomEditingIdentity.ValidOperation(new SaveRoomExplanation(row.Observation.EntityId,
                Observed(row.Values, row.Observation), row.Values), roomId))
                throw Invalid("Unusable explanation snapshot.");
        foreach (var row in subrooms)
            if (!RoomEditingIdentity.ValidOperation(new SaveRoomSubroom(row.Observation.EntityId,
                Observed(row.Values, row.Observation), row.Values), roomId)
                || row.Geometry is null) throw Invalid("Unusable subroom snapshot.");
        foreach (var row in transitions)
            if (!RoomEditingIdentity.ValidOperation(new SaveRoomTransition(row.Observation.EntityId,
                Observed(row.Values, row.Observation), row.Values), roomId)
                || row.Metadata is null) throw Invalid("Unusable transition snapshot.");
        foreach (var row in connections)
            if (!RoomEditingIdentity.ValidOperation(new SaveRoomConnection(row.Observation.EntityId,
                Observed(row.Values, row.Observation), row.Values), roomId))
                throw Invalid("Unusable connection snapshot.");
        foreach (var row in checks)
            if (!RoomEditingIdentity.ValidOperation(new SaveRoomCheck(row.Observation.EntityId,
                Observed(row.Values, row.Observation), row.Values), roomId)
                || row.Metadata is null) throw Invalid("Unusable check snapshot.");
    }

    private static RoomRowOrigin<T> Observed<T>(T values, RoomRowObservation observation) =>
        new(RoomOriginKind.Observed, values, observation, []);

    private static ImmutableArray<T> Replace<T>(ImmutableArray<T> original, ImmutableArray<T> replacements,
        ImmutableArray<Guid> removed, Func<T, Guid> id)
    {
        Unique(replacements, id);
        Unique(removed, x => x);
        var remove = removed.ToHashSet();
        if (replacements.Any(x => remove.Contains(id(x)))) throw Invalid("Replacement and removed IDs overlap.");
        var byId = replacements.ToDictionary(id);
        var result = ImmutableArray.CreateBuilder<T>();
        foreach (var value in original)
        {
            var key = id(value);
            if (remove.Contains(key)) continue;
            if (byId.Remove(key, out var replacement)) result.Add(replacement);
            else result.Add(value);
        }
        foreach (var value in replacements) if (byId.ContainsKey(id(value))) result.Add(value);
        return result.ToImmutable();
    }

    private static ImmutableArray<T> Order<T>(ImmutableArray<T> rows, RoomOrderReplacement replacement,
        Func<T, RoomRowObservation> observation)
    {
        if (replacement is null) throw Invalid("Missing order replacement.");
        Unique(replacement.Active, x => x);
        Unique(replacement.Archived, x => x);
        var ids = replacement.Active.AddRange(replacement.Archived);
        Unique(ids, x => x);
        var byId = rows.ToDictionary(x => observation(x).EntityId);
        if (ids.Length != rows.Length || ids.Any(x => !byId.ContainsKey(x))
            || replacement.Active.Any(x => observation(byId[x]).IsArchived)
            || replacement.Archived.Any(x => !observation(byId[x]).IsArchived))
            throw Invalid("Incomplete or mismatched active/archive order replacement.");
        return ids.Select(x => byId[x]).ToImmutableArray();
    }

    private static void Unique<T>(ImmutableArray<T> values, Func<T, Guid> id)
    {
        if (values.IsDefault) throw Invalid("Missing authoritative collection.");
        var seen = new HashSet<Guid>();
        foreach (var value in values)
            if (value is null || id(value) == Guid.Empty || !seen.Add(id(value)))
                throw Invalid("Unusable or duplicate authoritative identity.");
    }

    private static JsonException Invalid(string message) => new(message);
}
