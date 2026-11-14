using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class SceneImportService
{
    public async Task<SceneApplyResult> ApplyV3Async(SceneApplyRequest request)
    {
        ValidateMutations(request);
        using var imageAdmission = sceneImages is null ? null : await sceneImages.AcquireLifecycleAsync(CancellationToken.None);
        using var publication = await publications.AcquireAsync();
        var identity = await admission.AuthorAsync(CancellationToken.None);
        var owner = identity.Owner;
        // Request cancellation stops body admission, not an admitted atomic write.
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, identity.Actor, CancellationToken.None);
        var current = await ReadV3EvidenceAsync(db, request.SceneName, [], request.DestinationKeys, owner, CancellationToken.None);
        if (current.Fingerprint != request.Fingerprint || current.Destinations.Fingerprint != request.DestinationFingerprint)
            throw new SceneEvidenceStaleException();
        var matching = current.Preview.MatchingRooms;
        Room room;
        if (request.Target.ExistingId is { } roomId)
        {
            if (!matching.Any(r => r.Id == roomId)) throw Ineligible("The selected target room is no longer an active scene-name match.");
            room = await db.Rooms.SingleAsync(r => r.Id == roomId);
        }
        else
        {
            if (matching.Count != 0 || string.IsNullOrWhiteSpace(request.SceneName)) throw Ineligible("A new room requires a nonblank unmatched scene identity.");
            room = new Room { FriendlyName = request.Target.NewFriendlyName!, InGameId = request.Target.NewInGameId,
                ReferenceId = await CreateUniqueReferenceIdAsync(db, request.SceneName, CancellationToken.None),
                SortOrder = await NextSortOrderAsync(db.Rooms.Where(r => !r.IsArchived && r.RoomGroupId == null), CancellationToken.None) };
            db.Rooms.Add(room);
        }
        var transitions = await db.RoomTransitions.Where(t => !t.IsArchived && t.RoomId == room.Id).ToDictionaryAsync(t => t.Id);
        var checks = await db.CheckLocations.Where(t => !t.IsArchived && t.RoomId == room.Id).ToDictionaryAsync(t => t.Id);
        RoomTransition Transition(Guid id) => transitions.TryGetValue(id, out var t) ? t : throw Ineligible("A transition target is no longer active in this room.");
        CheckLocation Check(Guid id, bool enemy) => checks.TryGetValue(id, out var c) && CheckLocationTypeCatalogue.IsEnemy(c.LocationType) == enemy
            ? c : throw Ineligible("A check target is no longer active in this room/category.");
        // Validate all current ownership/category constraints before mutation.
        foreach (var u in request.TransitionUpdates) _ = Transition(u.Id);
        foreach (var u in request.CheckUpdates) _ = Check(u.Id, false);
        foreach (var u in request.EnemyUpdates) _ = Check(u.Id, true);
        foreach (var a in request.Archives)
            if (a.Family == SceneMutationFamily.Transition) _ = Transition(a.Id); else _ = Check(a.Id, a.Family == SceneMutationFamily.Enemy);
        if (request.Bounds is { } b)
        {
            if (b.Original != new SceneBoundsObservation(room.SceneMinX, room.SceneMinY, room.SceneMaxX, room.SceneMaxY)) throw new SceneEvidenceStaleException();
            if (b.Kind == SceneBoundsMutationKind.Initialize ? room.SceneMaxX is not null || room.SceneMaxY is not null
                : room.SceneMaxX is not { } mx || room.SceneMaxY is not { } my || !double.IsFinite(mx) || !double.IsFinite(my) || mx <= 0 || my <= 0)
                throw Ineligible("The bounds operation is no longer eligible.");
            if (!SceneBounds.TryGetSize(room.SceneMinX, room.SceneMinY, b.MaxX, b.MaxY, out _, out _))
                throw Ineligible("Imported scene maxima must exceed the retained minima and produce finite positive dimensions.");
        }
        foreach (var a in request.Archives)
            if (a.Family == SceneMutationFamily.Transition) Transition(a.Id).IsArchived = true; else Check(a.Id, a.Family == SceneMutationFamily.Enemy).IsArchived = true;
        foreach (var u in request.TransitionUpdates) SetMetadata(Transition(u.Id), u.Metadata);
        foreach (var u in request.CheckUpdates) SetMetadata(Check(u.Id, false), u.Metadata);
        var validateChecks = new List<CheckLocation>();
        foreach (var u in request.EnemyUpdates)
        {
            var c = Check(u.Id, true); SetMetadata(c, u.Metadata);
            if (c.Requirements != u.Requirements) validateChecks.Add(c);
            c.Requirements = u.Requirements; c.Notes = u.Notes;
        }
        var createdTransitions = new List<RoomTransition>(); var createdChecks = new List<CheckLocation>();
        var transitionOrder = await NextSortOrderAsync(db.RoomTransitions.Where(t => !t.IsArchived && t.RoomId == room.Id), CancellationToken.None);
        var checkOrder = await NextSortOrderAsync(db.CheckLocations.Where(t => !t.IsArchived && t.RoomId == room.Id), CancellationToken.None);
        foreach (var c in request.TransitionCreates)
        {
            var t = new RoomTransition { RoomId = room.Id, FriendlyName = c.FriendlyName, Requirements = c.Requirements,
                AnnotationSceneUnitX = c.Annotation.X, AnnotationSceneUnitY = c.Annotation.Y, EnableAnnotation = true,
                DestinationRoomReferenceText = c.Destination.NewTargetRoom ? room.ReferenceId : c.Destination.RoomReference,
                DestinationTransitionAliasText = c.Destination.TransitionAlias, SortOrder = transitionOrder++ };
            SetMetadata(t, c.Metadata); createdTransitions.Add(t);
        }
        foreach (var c in request.CheckCreates)
        {
            var t = new CheckLocation { RoomId = room.Id, FriendlyName = c.FriendlyName, Requirements = c.Requirements, Notes = c.Notes,
                LocationType = c.LocationType, AnnotationSceneUnitX = c.Annotation.X, AnnotationSceneUnitY = c.Annotation.Y,
                EnableAnnotation = true, SortOrder = checkOrder++ };
            SetMetadata(t, c.Metadata); createdChecks.Add(t);
        }
        if (request.Bounds is { } bounds) SetMaxima(room, bounds.MaxX, bounds.MaxY);
        db.AddRange(createdTransitions); db.AddRange(createdChecks);
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        var context = await requirementStatus.LoadContextAsync(db, CancellationToken.None);
        foreach (var t in createdTransitions) t.RequirementsParseSucceeded = requirementStatus.ValidateSafely(context, room.Id, t.Id, RequirementBearingRowKind.Transition, t.Requirements);
        foreach (var c in createdChecks.Concat(validateChecks)) c.RequirementsParseSucceeded = requirementStatus.ValidateSafely(context, room.Id, c.Id, RequirementBearingRowKind.Check, c.Requirements);
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync();
        if (await LogicCatalogService.TryAutoAssignRoomGroupFromMapZoneAsync(db, room, CancellationToken.None)) await db.SaveChangesAsync();
        await transaction.FinalizeAsync(CancellationToken.None);
        var receipt = new SceneAppliedReceipt(room.Id, createdTransitions.Select(t => t.Id).ToArray(), createdChecks.Select(t => t.Id).ToArray(),
            request.TransitionUpdates.Select(u => u.Id).Concat(createdTransitions.Select(t => t.Id)).ToArray());
        var plan = await SceneAffectedPublication.PrepareAsync(db, publication, transaction, [room.Id]);
        var committed = new SceneApplyResult(receipt, null, plan.Recovery);
        await transaction.CommitAsync(() => publication.PublishCommitted(plan.Publication));
        try
        {
            // Detach the committed transaction before the fallible read snapshot.
            await transaction.DisposeAsync();
            await using var snapshot = await db.Database.BeginTransactionAsync();
            return committed with { Patch = await SceneAffectedPublication.ProjectAsync(db, plan, sceneImages) };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scene import committed; affected scalar projection unavailable.");
            return committed;
        }
    }

    private static void ValidateMutations(SceneApplyRequest r)
    {
        if (r.SceneName is null || string.IsNullOrEmpty(r.Fingerprint) || r.Target is null || r.Target.ExistingId == Guid.Empty ||
            r.DestinationKeys is null || r.DestinationKeys.Any(k => k is null) || r.DestinationKeys.Distinct(StringComparer.Ordinal).Count() != r.DestinationKeys.Count ||
            string.IsNullOrEmpty(r.DestinationFingerprint) || r.TransitionCreates is null || r.TransitionUpdates is null || r.CheckCreates is null ||
            r.CheckUpdates is null || r.EnemyUpdates is null || r.Archives is null) throw new SceneTransportException();
        if (r.Target.ExistingId is null ? r.Target.NewFriendlyName != r.SceneName || r.Target.NewInGameId != r.SceneName
            : r.Target.NewFriendlyName is not null || r.Target.NewInGameId is not null) throw new SceneTransportException();
        foreach (var c in r.TransitionCreates)
        {
            if (c is null || c.FriendlyName is null || c.Requirements is not ("" or "Act 3") || c.Destination is null ||
                c.Destination.NewTargetRoom && (r.Target.ExistingId is not null || c.Destination.RoomReference is not null)) throw new SceneTransportException();
            Metadata(c.Metadata); Annotation(c.Annotation, c.Metadata);
        }
        foreach (var c in r.CheckCreates)
        {
            if (c is null || c.FriendlyName is null || c.Requirements is null || c.Notes is null || c.LocationType is not (null or "enemy") ||
                c.LocationType is null && (c.Notes != "" || c.Requirements is not ("" or "Act 3"))) throw new SceneTransportException();
            Metadata(c.Metadata); Annotation(c.Annotation, c.Metadata);
        }
        foreach (var u in r.TransitionUpdates) { if (u is null) throw new SceneTransportException(); Metadata(u.Metadata); }
        foreach (var u in r.CheckUpdates) { if (u is null) throw new SceneTransportException(); Metadata(u.Metadata); }
        foreach (var u in r.EnemyUpdates) { if (u is null || u.Requirements is null || u.Notes is null) throw new SceneTransportException(); Metadata(u.Metadata); }
        if (r.Archives.Any(a => a is null || !Enum.IsDefined(a.Family))) throw new SceneTransportException();
        var ids = r.TransitionUpdates.Select(u => u.Id).Concat(r.CheckUpdates.Select(u => u.Id)).Concat(r.EnemyUpdates.Select(u => u.Id)).Concat(r.Archives.Select(a => a.Id)).ToArray();
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) throw new SceneTransportException();
        if (r.Bounds is { } b && (b.Original is null || !Enum.IsDefined(b.Kind) || !double.IsFinite(b.MaxX) || !double.IsFinite(b.MaxY) || b.MaxX <= 0 || b.MaxY <= 0 ||
            !double.IsFinite(b.Original.MinX) || !double.IsFinite(b.Original.MinY) || !SceneBounds.FiniteOrNull(b.Original.MaxX) || !SceneBounds.FiniteOrNull(b.Original.MaxY))) throw new SceneTransportException();

        static void Metadata(SceneMetadata m)
        {
            if (m is null || new[] { m.X, m.Y, m.Z, m.LocalX, m.LocalY, m.LocalZ }.Any(v => !SceneBounds.FiniteOrNull(v))) throw new SceneTransportException();
        }
        static void Annotation(SceneInitialAnnotation a, SceneMetadata m)
        {
            var pair = m.X is not null && m.Y is not null;
            if (a is null || !a.Enabled || a.X != (pair ? m.X : null) || a.Y != (pair ? m.Y : null)) throw new SceneTransportException();
        }
    }
    private static SceneWorkbenchValidationException Ineligible(string text) => new(text);
    private static void SetMetadata(RoomTransition t, SceneMetadata m)
    { t.InGameId = m.InGameId; t.InGamePositionX = m.X; t.InGamePositionY = m.Y; t.InGamePositionZ = m.Z; t.LocalPositionX = m.LocalX; t.LocalPositionY = m.LocalY; t.LocalPositionZ = m.LocalZ; }
    private static void SetMetadata(CheckLocation t, SceneMetadata m)
    { t.InGameId = m.InGameId; t.InGamePositionX = m.X; t.InGamePositionY = m.Y; t.InGamePositionZ = m.Z; t.LocalPositionX = m.LocalX; t.LocalPositionY = m.LocalY; t.LocalPositionZ = m.LocalZ; }
}
