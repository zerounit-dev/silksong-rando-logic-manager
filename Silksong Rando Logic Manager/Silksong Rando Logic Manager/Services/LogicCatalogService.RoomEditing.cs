using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

// R3-only entry points use the retained domain helpers with a fresh transactional
// merge and a reliable receipt. The public V2 save signatures and transactions are
// untouched. No catch-all adapter is used to infer whether a write committed.
public sealed partial class LogicCatalogService
{
    internal async Task<RoomMutationEffect?> SaveTrackedConnectionAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation as Wire.SaveRoomConnection
            ?? throw new RoomTrackingProtocolException("The head is not a connection save.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var row = await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == operation.EntityId
            && x.RoomId == tracker.Identity.RoomId && !x.IsArchived);
        if (row is null) { RoomEditingMutationScope.Missing(tracker, operation.EntityId); return null; }
        var current = ConnectionValues(row);
        var submitted = operation.Submitted with { Alias = Wire.RoomAuthoredAlias.Commit(operation.Submitted.Alias, operation.Origin.Original.Alias) };
        var merge = Wire.RoomEditingMerge.Connection(operation.Origin.Original, submitted, current, tracker.ConnectionReview);
        if (!merge.CanApply)
        {
            tracker.ConnectionReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                merge.ReviewSuperseded ? "The reviewed connection values changed again. Review the current values." : "This connection changed elsewhere.",
                new Wire.RoomConnectionConflictEvidence(current, operation.Submitted,
                    Wire.RoomEditingMerge.Connection(operation.Origin.Original, submitted, current).DivergentFields));
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var baseline = new ConnectionFieldDiffMapper().Clone(row);
        var draft = new ConnectionFieldDiffMapper().Clone(row);
        var value = merge.Values;
        draft.Alias = value.Alias; draft.FriendlyName = value.FriendlyName;
        draft.SourceSubroomReferenceText = value.SourceSubroomReferenceText;
        draft.DestinationSubroomReferenceText = value.DestinationSubroomReferenceText;
        draft.Requirements = value.Requirements; draft.Notes = value.Notes;
        draft.IsTodo = value.IsTodo; draft.IsVerified = value.IsVerified;
        var mapper = new ConnectionFieldDiffMapper();
        var fields = mapper.Differences(baseline, draft).ToHashSet(StringComparer.Ordinal);
        var originalText = db.Entry(row).Properties.ToDictionary(x => x.Metadata.Name, x => x.OriginalValue as string, StringComparer.Ordinal);
        // Alias reconciliation consumes the actual durable group, never the
        // operation's projected baseline or a canonicalized sibling baseline.
        var siblings = fields.Contains(nameof(SubroomConnection.Alias))
            ? await db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == row.RoomId && !x.IsArchived).ToListAsync()
            : null;
        mapper.Apply(row, draft, fields);
        if (siblings is not null)
            ReconcileTrackedConnectionAlias(row, siblings);
        await ValidateResolvedOwnershipAsync(db, row, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        var resolves = RequiresResolution(row, fields);
        if (resolves) await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(row, fields, originalText, CancellationToken.None);
        if (fields.Contains(nameof(SubroomConnection.Requirements)))
            await ValidateRequirementsAsync(db, row, RequirementBearingRowKind.Connection, CancellationToken.None);
        var scene = RequiresSceneLayoutRefresh(row, fields)
            ? Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport : Wire.RoomSceneConcerns.None;
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Connections, scene);
    }

    internal async Task<RoomMutationEffect?> SaveTrackedCheckAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation as Wire.SaveRoomCheck
            ?? throw new RoomTrackingProtocolException("The head is not a check save.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var row = await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == operation.EntityId && x.RoomId == tracker.Identity.RoomId && !x.IsArchived);
        if (row is null) { RoomEditingMutationScope.Missing(tracker, operation.EntityId); return null; }
        var current = CheckValues(row);
        var merge = Wire.RoomEditingMerge.Check(operation.Origin.Original, operation.Submitted, current, tracker.CheckReview);
        if (!merge.CanApply)
        {
            tracker.CheckReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                merge.ReviewSuperseded ? "The reviewed check values changed again. Review the current values." : "This check changed elsewhere.",
                new Wire.RoomCheckConflictEvidence(current, operation.Submitted,
                    Wire.RoomEditingMerge.Check(operation.Origin.Original, operation.Submitted, current).DivergentFields));
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var mapper = new CheckFieldDiffMapper();
        var baseline = mapper.Clone(row); var draft = mapper.Clone(row); var value = merge.Values;
        draft.FriendlyName = value.FriendlyName; draft.SubroomReferenceText = value.SubroomReferenceText;
        draft.Requirements = value.Requirements; draft.Notes = value.Notes; draft.LocationType = value.LocationType;
        draft.IsTodo = value.IsTodo; draft.IsVerified = value.IsVerified;
        var fields = mapper.Differences(baseline, draft).ToHashSet(StringComparer.Ordinal);
        var originalText = db.Entry(row).Properties.ToDictionary(x => x.Metadata.Name, x => x.OriginalValue as string, StringComparer.Ordinal);
        var ordered = fields.Contains(nameof(CheckLocation.LocationType)) ? await OrderedActiveChecksAsync(db, row.RoomId, CancellationToken.None) : null;
        var priorEnemy = CheckLocationTypeCatalogue.IsEnemy(row.LocationType);
        mapper.Apply(row, draft, fields);
        if (ordered is not null) NormalizeCheckOrder(ordered,
            priorEnemy != CheckLocationTypeCatalogue.IsEnemy(row.LocationType) ? row.Id : null);
        await ValidateResolvedOwnershipAsync(db, row, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        if (RequiresResolution(row, fields))
            await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(row, fields, originalText, CancellationToken.None);
        if (fields.Contains(nameof(CheckLocation.Requirements)))
            await ValidateRequirementsAsync(db, row, RequirementBearingRowKind.Check, CancellationToken.None);
        var scene = fields.Overlaps([nameof(CheckLocation.FriendlyName), nameof(CheckLocation.SubroomReferenceText), nameof(CheckLocation.LocationType)])
            ? Wire.RoomSceneConcerns.Canvas : Wire.RoomSceneConcerns.None;
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Checks, scene);
    }

    internal async Task<RoomMutationEffect?> SaveTrackedMetadataAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var (id, origin, submitted, transition) = tracker.CurrentHead?.Operation switch
        {
            Wire.SaveRoomTransitionMetadata x => (x.EntityId, x.Origin, x.Submitted, true),
            Wire.SaveRoomCheckMetadata x => (x.EntityId, x.Origin, x.Submitted, false),
            _ => throw new RoomTrackingProtocolException("The head is not an in-game metadata save.")
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        RoomTransition? exit = transition ? await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId && !x.IsArchived) : null;
        CheckLocation? check = !transition ? await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId && !x.IsArchived) : null;
        if (exit is null && check is null) { RoomEditingMutationScope.Missing(tracker, id); return null; }
        var current = exit is not null ? Metadata(exit) : Metadata(check!);
        var merge = Wire.RoomEditingMerge.Metadata(origin.Original, submitted, current, tracker.MetadataReview);
        if (!merge.CanApply)
        {
            tracker.MetadataReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                merge.ReviewSuperseded ? "The reviewed metadata changed again. Review the current values." : "This metadata changed elsewhere.",
                new Wire.RoomMetadataConflictEvidence(current, submitted, Wire.RoomEditingMerge.Metadata(origin.Original, submitted, current).DivergentFields));
            return null;
        }
        var value = merge.Values;
        if (new[] { value.InGamePositionX, value.InGamePositionY, value.InGamePositionZ, value.LocalPositionX, value.LocalPositionY,
            value.LocalPositionZ, value.AnnotationSceneUnitX, value.AnnotationSceneUnitY }.Any(x => x.HasValue && !double.IsFinite(x.Value)))
        {
            RoomEditingMutationScope.Corrective(tracker, id, "In-game, local and annotation coordinates must be finite. Copy pending work before reloading.");
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        if (exit is not null) ApplyMetadata(exit, value); else ApplyMetadata(check!, value);
        await db.SaveChangesAsync(CancellationToken.None);
        var scene = value.AnnotationSceneUnitX != current.AnnotationSceneUnitX || value.AnnotationSceneUnitY != current.AnnotationSceneUnitY
            ? Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport : Wire.RoomSceneConcerns.None;
        return await scope.CompleteAsync(db, tx, transition ? RoomProjectionFamilies.Transitions : RoomProjectionFamilies.Checks, scene);
    }

    internal async Task<RoomMutationEffect?> SaveTrackedSubroomGeometryAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation as Wire.SaveRoomSubroomGeometry
            ?? throw new RoomTrackingProtocolException("The head is not a subroom rectangle save.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var row = await db.Subrooms.SingleOrDefaultAsync(x => x.Id == operation.EntityId && x.RoomId == tracker.Identity.RoomId && !x.IsArchived);
        if (row is null) { RoomEditingMutationScope.Missing(tracker, operation.EntityId); return null; }
        var current = new Wire.RoomSubroomGeometry(row.SceneUnitX, row.SceneUnitY, row.SceneUnitWidth, row.SceneUnitHeight, row.EnableAnnotation);
        var merge = Wire.RoomEditingMerge.Geometry(operation.Origin.Original, operation.Submitted, current, tracker.GeometryReview);
        if (!merge.CanApply)
        {
            tracker.GeometryReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                merge.ReviewSuperseded ? "The reviewed rectangle changed again. Review the current values." : "This rectangle changed elsewhere.",
                new Wire.RoomGeometryConflictEvidence(current, operation.Submitted,
                    Wire.RoomEditingMerge.Geometry(operation.Origin.Original, operation.Submitted, current).DivergentFields));
            return null;
        }
        var value = merge.Values;
        if (!(value.X is null && value.Y is null && value.Width is null && value.Height is null)
            && !ValidRectangle(value.X, value.Y, value.Width, value.Height))
        {
            RoomEditingMutationScope.Corrective(tracker, row.Id, "Subroom geometry must be all absent or complete, finite, and positive. Copy pending work before reloading.");
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        row.SceneUnitX = value.X; row.SceneUnitY = value.Y; row.SceneUnitWidth = value.Width; row.SceneUnitHeight = value.Height;
        // Rectangle edits do not overwrite visibility, even when it changed since
        // the originating gesture. Enablement has a separate typed operation.
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Subrooms, Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport);
    }

    internal async Task<RoomMutationEffect?> ScaffoldTrackedConnectionAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation as Wire.ScaffoldRoomConnectionInverse
            ?? throw new RoomTrackingProtocolException("The head is not a reverse-connection scaffold.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var source = await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == operation.SourceEntityId
            && x.RoomId == tracker.Identity.RoomId && !x.IsArchived);
        if (source is null) { RoomEditingMutationScope.Missing(tracker, operation.SourceEntityId); return null; }
        if (await db.SubroomConnections.AnyAsync(x => x.Id == operation.NewEntityId))
        {
            RoomEditingMutationScope.Corrective(tracker, operation.NewEntityId,
                "The permanent creation identity already exists. Nothing was created. Copy pending work before reloading.");
            return null;
        }
        var connections = await db.SubroomConnections.Where(x => x.RoomId == source.RoomId).ToListAsync();
        if (!Validation.CanScaffoldInverse(source, connections))
        {
            RoomEditingMutationScope.Corrective(tracker, source.Id,
                "This connection is no longer eligible for inverse scaffolding. Copy pending work before reloading.");
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var inverse = new SubroomConnection
        {
            Id = operation.NewEntityId, RoomId = source.RoomId, Alias = source.Alias, FriendlyName = source.FriendlyName,
            SourceSubroomReferenceText = source.DestinationSubroomReferenceText,
            DestinationSubroomReferenceText = source.SourceSubroomReferenceText, Requirements = string.Empty,
            Notes = string.Empty, IsTodo = false, IsVerified = null,
            SortOrder = await NextSortOrderAsync(db.SubroomConnections.Where(x => x.RoomId == source.RoomId && !x.IsArchived), CancellationToken.None)
        };
        db.SubroomConnections.Add(inverse);
        await db.SaveChangesAsync(CancellationToken.None);
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(inverse, CancellationToken.None);
        await ValidateRequirementsAsync(db, inverse, RequirementBearingRowKind.Connection, CancellationToken.None);
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Connections,
            Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport, createdId: inverse.Id);
    }

    private static Wire.RoomConnectionValues ConnectionValues(SubroomConnection x) => new(x.Alias, x.FriendlyName,
        x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified);
    private static void ReconcileTrackedConnectionAlias(SubroomConnection row, IReadOnlyList<SubroomConnection> before)
    {
        var original = before.Single(x => x.Id == row.Id);
        if (string.Equals(original.Alias.Trim(), row.Alias.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        // The canonical target is defined by durable order, not SQL enumeration.
        // Use the actual pre-write raw sibling facts; reading them repairs nothing.
        var target = before.Where(x => x.Id != row.Id
                && string.Equals(x.Alias.Trim(), row.Alias.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).FirstOrDefault();
        if (target is not null)
        {
            row.EnableAnnotation = target.EnableAnnotation;
            row.SceneUnitX = target.SceneUnitX;
            row.SceneUnitY = target.SceneUnitY;
        }
        else if (before.Any(x => x.Id != row.Id
            && string.Equals(x.Alias.Trim(), original.Alias.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            row.EnableAnnotation = false;
            row.SceneUnitX = null;
            row.SceneUnitY = null;
        }
        // A sole-row rename into a new alias retains the row's raw state.
    }
    private static Wire.RoomCheckValues CheckValues(CheckLocation x) => new(x.FriendlyName, x.SubroomReferenceText,
        x.Requirements, x.Notes, x.LocationType, x.IsTodo, x.IsVerified);
    private static Wire.RoomInGameMetadata Metadata(RoomTransition x) => new(x.InGameId, x.InGamePositionX, x.InGamePositionY,
        x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static Wire.RoomInGameMetadata Metadata(CheckLocation x) => new(x.InGameId, x.InGamePositionX, x.InGamePositionY,
        x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static void ApplyMetadata(RoomTransition x, Wire.RoomInGameMetadata v)
    {
        x.InGameId = v.InGameId; x.InGamePositionX = v.InGamePositionX; x.InGamePositionY = v.InGamePositionY;
        x.InGamePositionZ = v.InGamePositionZ; x.LocalPositionX = v.LocalPositionX; x.LocalPositionY = v.LocalPositionY;
        x.LocalPositionZ = v.LocalPositionZ; x.AnnotationSceneUnitX = v.AnnotationSceneUnitX; x.AnnotationSceneUnitY = v.AnnotationSceneUnitY;
    }
    private static void ApplyMetadata(CheckLocation x, Wire.RoomInGameMetadata v)
    {
        x.InGameId = v.InGameId; x.InGamePositionX = v.InGamePositionX; x.InGamePositionY = v.InGamePositionY;
        x.InGamePositionZ = v.InGamePositionZ; x.LocalPositionX = v.LocalPositionX; x.LocalPositionY = v.LocalPositionY;
        x.LocalPositionZ = v.LocalPositionZ; x.AnnotationSceneUnitX = v.AnnotationSceneUnitX; x.AnnotationSceneUnitY = v.AnnotationSceneUnitY;
    }
}
