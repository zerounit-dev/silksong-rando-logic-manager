using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Microsoft.EntityFrameworkCore;

namespace Silksong_Rando_Logic_Manager.Services;

public interface IRoomEditorV2CommandService
{
    Task<V2RoomHeaderCommandOutcome> SaveRoomHeaderAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft) => throw new NotSupportedException();
    Task<V2RoomHeaderCommandOutcome> PrepareRoomReferenceRenameAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft) => SaveRoomHeaderAsync(roomId, baseline, draft);
    Task<V2RoomHeaderCommandOutcome> ApplyRoomReferenceRenameAsync(Guid roomId, RoomReferenceRenameProposal proposal, bool updateReferences) => throw new NotSupportedException();
    V2RoomHeaderCommandOutcome RevertRoomReferenceRename(RoomReferenceRenameProposal proposal) => throw new NotSupportedException();
    Task<V2RoomLifecycleCommandOutcome> SetRoomArchiveAsync(Guid roomId, bool archived) => throw new NotSupportedException();
    Task<V2RoomLifecycleCommandOutcome> DeleteRoomAsync(Guid roomId) => throw new NotSupportedException();
    Task<V2RoomSceneDimensionsCommandOutcome> SaveRoomSceneDimensionsAsync(Guid roomId, RoomSceneDimensionsDurableBaseline baseline, RoomSceneDimensionsDraft draft) => throw new NotSupportedException();
    Task<V2SceneImageCaptureCommandOutcome> PrepareSceneImageCaptureAsync(Guid roomId) => throw new NotSupportedException();
    Task<V2SceneImageCaptureCommandOutcome> ResetSceneImageCaptureEstimateAsync(Guid roomId) => throw new NotSupportedException();
    Task<V2SceneImageCaptureCommandOutcome> ApplySceneImageCaptureAsync(Guid roomId, SceneImageCaptureDraft draft) => throw new NotSupportedException();
    Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft);
    Task<V2SubroomCommandOutcome> PrepareSubroomReferenceRenameAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => SaveSubroomAsync(roomId, baseline, draft);
    Task<V2SubroomCommandOutcome> ApplySubroomReferenceRenameAsync(Guid roomId, SubroomReferenceRenameProposal proposal, bool updateReferences) => throw new NotSupportedException();
    V2SubroomCommandOutcome RevertSubroomReferenceRename(SubroomReferenceRenameProposal proposal) => throw new NotSupportedException();
    Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft);
    Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex);
    Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived);
    Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId);
    Task<V2SubroomCommandOutcome> SaveSubroomGeometryAsync(Guid roomId, SubroomDurableBaseline baseline, double? x, double? y, double? width, double? height) => throw new NotSupportedException();
    Task<V2SubroomCommandOutcome> ClearSubroomAnnotationAsync(Guid roomId, SubroomDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2SubroomCommandOutcome> ShowSubroomAnnotationAsync(Guid roomId, SubroomDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2SubroomCommandOutcome> HideSubroomAnnotationAsync(Guid roomId, SubroomDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> SaveCheckAsync(Guid roomId, CheckDurableBaseline baseline, CheckDraft draft) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> SaveCheckMetadataAsync(Guid roomId, CheckMetadataDurableBaseline baseline, CheckInGameMetadataDraft draft) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> PlaceCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline, double x, double y) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> MoveCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline, double x, double y) => PlaceCheckAnnotationAsync(roomId, baseline, x, y);
    Task<V2CheckCommandOutcome> RemoveCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> ShowCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> ClearCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> ResetCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> ShowAndSelectCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> CreateCheckAsync(Guid roomId, CheckDraft draft) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> ReorderCheckAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> SetCheckArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
    Task<V2CheckCommandOutcome> DeleteCheckAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> SaveTransitionAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> SaveTransitionMetadataAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionInGameMetadataDraft draft) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> PlaceTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline, double x, double y) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> MoveTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline, double x, double y) => PlaceTransitionAnnotationAsync(roomId, baseline, x, y);
    Task<V2TransitionCommandOutcome> RemoveTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ShowTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ClearTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ResetTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ShowAndSelectTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> CreateTransitionAsync(Guid roomId, TransitionDraft draft) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> PrepareCreateTransitionInverseAsync(Guid roomId, TransitionDraft draft) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ReorderTransitionAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> SetTransitionArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> DeleteTransitionAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> PrepareInverseAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> PrepareInverseStatusActionAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ApplyInverseAsync(Guid roomId, TransitionInverseProposal proposal, bool updateInverse) => throw new NotSupportedException();
    Task<V2TransitionCommandOutcome> ApplyCreateInverseAsync(Guid roomId, TransitionInverseCreateProposal proposal, bool updateInverse) => throw new NotSupportedException();
    V2TransitionCommandOutcome RevertInverse(TransitionInverseProposal proposal) => throw new NotSupportedException();
    V2TransitionCommandOutcome RevertCreateInverse(TransitionInverseCreateProposal proposal) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> SaveConnectionAsync(Guid roomId, ConnectionDurableBaseline baseline, ConnectionDraft draft) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> CreateConnectionAsync(Guid roomId, ConnectionDraft draft) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> ReorderConnectionAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> SetConnectionArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> DeleteConnectionAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> ScaffoldInverseConnectionAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> PlaceConnectionAnnotationAsync(Guid roomId, Guid entityId, double x, double y) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> MoveConnectionAnnotationAsync(Guid roomId, Guid entityId, double x, double y) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> DisableConnectionAnnotationAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> ClearConnectionAnnotationAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> ShowConnectionAnnotationAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    Task<V2ConnectionCommandOutcome> ShowAndSelectConnectionAnnotationAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
}

/// <summary>V2-only typed adapter.  Entity-shaped V1 patches never cross this boundary.</summary>
public sealed class RoomEditorV2CommandService(LogicCatalogService catalog, RequirementValidationService requirementValidation,
    IDbContextFactory<LogicDbContext>? contexts = null, ConnectionRoomSaveCoordinator? connectionCoordinator = null,
    SceneImageCaptureService? sceneImages = null) : IRoomEditorV2CommandService
{
    private readonly ConnectionRoomSaveCoordinator connections = connectionCoordinator ?? new ConnectionRoomSaveCoordinator(catalog);
    private readonly RequirementValidationService requirementStatus = requirementValidation;
    public async Task<V2SceneImageCaptureCommandOutcome> PrepareSceneImageCaptureAsync(Guid roomId)
    {
        try { if (sceneImages is null) throw new InvalidOperationException("The scene-image capture service is unavailable."); var result = await sceneImages.GetDraftAsync(roomId); return new(V2SceneImageCaptureCommandStatus.Committed, result.Information, result.Draft); }
        catch (InvalidOperationException ex) { return new(V2SceneImageCaptureCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2SceneImageCaptureCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2SceneImageCaptureCommandOutcome> ResetSceneImageCaptureEstimateAsync(Guid roomId)
    {
        try { if (sceneImages is null) throw new InvalidOperationException("The scene-image capture service is unavailable."); var result = await sceneImages.GetEstimateAsync(roomId); return new(V2SceneImageCaptureCommandStatus.Committed, result.Information, result.Draft); }
        catch (InvalidOperationException ex) { return new(V2SceneImageCaptureCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2SceneImageCaptureCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2SceneImageCaptureCommandOutcome> ApplySceneImageCaptureAsync(Guid roomId, SceneImageCaptureDraft draft)
    {
        try { if (sceneImages is null) throw new InvalidOperationException("The scene-image capture service is unavailable."); await sceneImages.ApplyAsync(roomId, draft); return new(V2SceneImageCaptureCommandStatus.Committed); }
        catch (InvalidOperationException ex) { return new(V2SceneImageCaptureCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2SceneImageCaptureCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2RoomSceneDimensionsCommandOutcome> SaveRoomSceneDimensionsAsync(Guid roomId, RoomSceneDimensionsDurableBaseline baseline, RoomSceneDimensionsDraft draft)
    {
        if (!ValidDimensionsDraft(draft)) return new(V2RoomSceneDimensionsCommandStatus.ExpectedFailure, "Enter both finite positive dimensions, or leave both blank.", null, draft);
        if (baseline.RoomId != roomId) return new(V2RoomSceneDimensionsCommandStatus.Missing, "This room is not loaded.", null, draft);
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 scene-dimensions command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived);
            if (room is null) return new(V2RoomSceneDimensionsCommandStatus.Missing, "This active room no longer exists.", null, draft);
            if (room.UpdatedUtc != baseline.UpdatedUtc || room.SceneUnitWidth != baseline.Width || room.SceneUnitHeight != baseline.Height)
                return new(V2RoomSceneDimensionsCommandStatus.Conflict, "The room scene dimensions changed elsewhere.", new(room.Id, room.UpdatedUtc, room.SceneUnitWidth, room.SceneUnitHeight), draft);
            if (room.SceneUnitWidth == draft.Width && room.SceneUnitHeight == draft.Height) return new(V2RoomSceneDimensionsCommandStatus.Unchanged);
            var hasTransform = room.SceneImageScaleXPercent is not null && room.SceneImageScaleYPercent is not null && room.SceneImagePanXPercent is not null && room.SceneImagePanYPercent is not null;
            room.SceneUnitWidth = draft.Width; room.SceneUnitHeight = draft.Height;
            if (draft.Width is not null && draft.Height is not null && hasTransform) room.IsSceneImageStale = true;
            var updated = DateTime.UtcNow; room.UpdatedUtc = updated <= room.UpdatedUtc ? room.UpdatedUtc.AddTicks(1) : updated;
            await db.SaveChangesAsync();
            return new(V2RoomSceneDimensionsCommandStatus.Committed);
        }
        catch (Exception ex) { return new(V2RoomSceneDimensionsCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    private static bool ValidDimensionsDraft(RoomSceneDimensionsDraft draft) => draft.Width is null && draft.Height is null || draft.Width is { } width && draft.Height is { } height && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
    public async Task<V2ConnectionCommandOutcome> PlaceConnectionAnnotationAsync(Guid roomId, Guid entityId, double x, double y)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 connection-placement command requires a database context factory.");
            // Admission is page-room scoped.  Verify it before taking the alias-group
            // coordinator lease so a stale/wrong-room scene callback cannot mutate a
            // connection owned by another displayed room.
            await using var db = await contexts.CreateDbContextAsync();
            var target = await db.SubroomConnections.AsNoTracking()
                .Where(row => row.Id == entityId)
                .Select(row => new { row.RoomId, row.IsArchived })
                .SingleOrDefaultAsync();
            if (target is null || target.RoomId != roomId)
                return new(V2ConnectionCommandStatus.Missing, "This connection is no longer in the current room.");
            if (target.IsArchived)
                return new(V2ConnectionCommandStatus.ExpectedFailure, "This connection is no longer active in the current room.");
            await connections.PlaceAnnotationAsync(entityId, roomId, x, y);
            return new(V2ConnectionCommandStatus.Committed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2ConnectionCommandOutcome> MoveConnectionAnnotationAsync(Guid roomId, Guid entityId, double x, double y)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 connection-move command requires a database context factory.");
            if (!double.IsFinite(x) || !double.IsFinite(y)) return new(V2ConnectionCommandStatus.ExpectedFailure, "A connection marker move requires finite coordinates.");
            await connections.MoveAnnotationAsync(entityId, roomId, x, y);
            return new(V2ConnectionCommandStatus.Committed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport);
        }
        catch (ConnectionAnnotationMoveMissingException ex) { return new(V2ConnectionCommandStatus.Missing, ex.Message); }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2ConnectionCommandOutcome> DisableConnectionAnnotationAsync(Guid roomId, Guid entityId)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 connection-disable command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var target = await db.SubroomConnections.AsNoTracking()
                .Where(row => row.Id == entityId)
                .Select(row => new { row.RoomId, row.IsArchived, row.EnableAnnotation })
                .SingleOrDefaultAsync();
            if (target is null || target.RoomId != roomId)
                return new(V2ConnectionCommandStatus.Missing, "This connection is no longer in the current room.");
            if (target.IsArchived)
                return new(V2ConnectionCommandStatus.ExpectedFailure, "This connection is no longer active in the current room.");
            if (!target.EnableAnnotation)
                return new(V2ConnectionCommandStatus.Unchanged);
            await connections.RemoveAnnotationAsync(entityId, roomId);
            return new(V2ConnectionCommandStatus.Committed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2ConnectionCommandOutcome> ClearConnectionAnnotationAsync(Guid roomId, Guid entityId)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 connection-clear command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var target = await db.SubroomConnections.AsNoTracking().Where(row => row.Id == entityId)
                .Select(row => new { row.RoomId, row.IsArchived, row.Alias }).SingleOrDefaultAsync();
            if (target is null || target.RoomId != roomId) return new(V2ConnectionCommandStatus.Missing, "This connection is no longer in the current room.");
            if (target.IsArchived) return new(V2ConnectionCommandStatus.ExpectedFailure, "This connection is no longer active in the current room.");
            var hasGroupGeometry = await db.SubroomConnections.AsNoTracking().AnyAsync(row => row.RoomId == roomId && !row.IsArchived &&
                row.Alias.Trim().ToLower() == target.Alias.Trim().ToLower() && (row.SceneUnitX != null || row.SceneUnitY != null));
            if (!hasGroupGeometry) return new(V2ConnectionCommandStatus.Unchanged);
            await connections.ClearAnnotationAsync(entityId, roomId);
            return new(V2ConnectionCommandStatus.Committed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2ConnectionCommandOutcome> ShowConnectionAnnotationAsync(Guid roomId, Guid entityId)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 connection-show command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var target = await db.SubroomConnections.AsNoTracking().Where(row => row.Id == entityId)
                .Select(row => new { row.RoomId, row.IsArchived, row.EnableAnnotation }).SingleOrDefaultAsync();
            if (target is null || target.RoomId != roomId) return new(V2ConnectionCommandStatus.Missing, "This connection is no longer in the current room.");
            if (target.IsArchived) return new(V2ConnectionCommandStatus.Missing, "This connection is no longer active in the current room.");
            if (target.EnableAnnotation) return new(V2ConnectionCommandStatus.Unchanged);
            await connections.ShowAnnotationAsync(entityId, roomId);
            return new(V2ConnectionCommandStatus.Committed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2ConnectionCommandOutcome> ShowAndSelectConnectionAnnotationAsync(Guid roomId, Guid entityId)
    {
        var result = await ShowConnectionAnnotationAsync(roomId, entityId);
        return result.Status == V2ConnectionCommandStatus.Committed
            ? result with { PostRefreshSelection = new(V2AnnotationSelectionKind.Connection, entityId) }
            : result;
    }
    public async Task<V2RoomHeaderCommandOutcome> SaveRoomHeaderAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft)
    {
        // Reference ID is never an ordinary header save: its pre-commit proposal
        // captures resolved-ID targets before any source text can change.
        if (draft.ReferenceId != baseline.ReferenceId) return await PrepareRoomReferenceRenameAsync(roomId, baseline, draft);
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 header command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var durable = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.RoomId && x.Id == roomId);
            if (durable is null) return new(V2RoomHeaderCommandStatus.Missing, "This room no longer exists.", null, draft);
            var changed = HeaderDifferences(baseline, draft);
            var externallyChanged = new[] { nameof(Room.FriendlyName), nameof(Room.InGameId), nameof(Room.Contributors), nameof(Room.Comments) }
                .Where(field => HeaderValue(baseline, field) != HeaderValue(durable, field)).ToHashSet(StringComparer.Ordinal);
            if (durable.UpdatedUtc != baseline.UpdatedUtc && changed.Overlaps(externallyChanged))
                return new(V2RoomHeaderCommandStatus.Conflict, "This room changed elsewhere.", HeaderBaseline(durable), draft);
            if (changed.Count == 0) return new(V2RoomHeaderCommandStatus.Unchanged);
            // The version read above is also guarded by this UPDATE.  A second
            // writer cannot slip between comparison and commit and overwrite an
            // authored field after the overlap check.
            var updatedUtc = DateTime.UtcNow;
            if (updatedUtc <= durable.UpdatedUtc) updatedUtc = durable.UpdatedUtc.AddTicks(1);
            var values = new List<object>();
            var assignments = new List<string>();
            foreach (var field in changed)
            {
                var value = HeaderValue(draft, field);
                if (value is null) assignments.Add($"[{field}] = NULL");
                else { assignments.Add($"[{field}] = {{{values.Count}}}"); values.Add(value); }
            }
            assignments.Add($"[UpdatedUtc] = {{{values.Count}}}"); values.Add(updatedUtc);
            var roomParameter = values.Count; values.Add(roomId);
            var versionParameter = values.Count; values.Add(durable.UpdatedUtc);
            var sql = $"UPDATE [Rooms] SET {string.Join(", ", assignments)} WHERE [Id] = {{{roomParameter}}} AND [UpdatedUtc] = {{{versionParameter}}}";
            if (await db.Database.ExecuteSqlRawAsync(sql, values.ToArray()) == 0)
            {
                var fresh = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId);
                return fresh is null
                    ? new(V2RoomHeaderCommandStatus.Missing, "This room no longer exists.", null, draft)
                    : new(V2RoomHeaderCommandStatus.Conflict, "This room changed elsewhere.", HeaderBaseline(fresh), draft);
            }
            return new(V2RoomHeaderCommandStatus.Committed);
        }
        catch (Exception ex) { return new(V2RoomHeaderCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public async Task<V2RoomHeaderCommandOutcome> PrepareRoomReferenceRenameAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft)
    {
        if (draft.ReferenceId == baseline.ReferenceId) return await SaveRoomHeaderAsync(roomId, baseline, draft);
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 reference proposal requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var source = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId && x.Id == baseline.RoomId);
            if (source is null) return new(V2RoomHeaderCommandStatus.Missing, "This room no longer exists.", null, draft);
            var changed = HeaderDifferences(baseline, draft);
            var external = HeaderFields.Where(field => HeaderValue(baseline, field) != HeaderValue(source, field)).ToHashSet(StringComparer.Ordinal);
            if (source.UpdatedUtc != baseline.UpdatedUtc && changed.Overlaps(external)) return new(V2RoomHeaderCommandStatus.Conflict, "This room changed elsewhere.", HeaderBaseline(source), draft);
            var transitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedDestinationRoomId == roomId)
                .Select(x => new RoomReferenceTransitionTargetEvidence(x.Id, x.UpdatedUtc, x.DestinationRoomReferenceText)).ToListAsync();
            var scenes = await db.MapScenes.AsNoTracking().Where(x => x.ResolvedRoomId == roomId)
                .Select(x => new RoomReferenceMapSceneTargetEvidence(x.Id, x.RoomReferenceText)).ToListAsync();
            return new(V2RoomHeaderCommandStatus.Proposal, Proposal: new(baseline, draft, transitions, scenes));
        }
        catch (Exception ex) { return new(V2RoomHeaderCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public async Task<V2RoomHeaderCommandOutcome> ApplyRoomReferenceRenameAsync(Guid roomId, RoomReferenceRenameProposal proposal, bool updateReferences)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 reference proposal requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
            // Map-scene resolution is the settled capture-context input.  Preserve
            // its actual pre-commit values so the post-resolution result, rather
            // than the proposal choice or route name, classifies scene refresh.
            var mapResolutionBefore = await db.MapScenes.AsNoTracking()
                .Select(x => new { x.Id, x.ResolvedRoomId }).ToDictionaryAsync(x => x.Id, x => x.ResolvedRoomId);
            var source = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId && x.Id == proposal.SourceBaseline.RoomId);
            if (source is null) return new(V2RoomHeaderCommandStatus.Missing, "This room no longer exists.", null, proposal.SourceCurrent);
            var changed = HeaderDifferences(proposal.SourceBaseline, proposal.SourceCurrent);
            var external = HeaderFields.Where(field => HeaderValue(proposal.SourceBaseline, field) != HeaderValue(source, field)).ToHashSet(StringComparer.Ordinal);
            if (source.UpdatedUtc != proposal.SourceBaseline.UpdatedUtc && changed.Overlaps(external)) return new(V2RoomHeaderCommandStatus.Conflict, "This room changed elsewhere.", HeaderBaseline(source), proposal.SourceCurrent);
            var transitionTargets = new List<RoomTransition>(); var sceneTargets = new List<MapScene>();
            if (updateReferences)
            {
                foreach (var evidence in proposal.TransitionTargets)
                {
                    var target = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == evidence.EntityId);
                    if (target is null || target.IsArchived || target.UpdatedUtc != evidence.UpdatedUtc || target.DestinationRoomReferenceText != evidence.ExpectedDestinationRoomReferenceText)
                        return new(V2RoomHeaderCommandStatus.Conflict, "A captured reference changed elsewhere.", HeaderBaseline(source), proposal.SourceCurrent);
                    transitionTargets.Add(target);
                }
                foreach (var evidence in proposal.MapSceneTargets)
                {
                    var target = await db.MapScenes.SingleOrDefaultAsync(x => x.Id == evidence.EntityId);
                    if (target is null || target.RoomReferenceText != evidence.ExpectedRoomReferenceText)
                        return new(V2RoomHeaderCommandStatus.Conflict, "A captured reference changed elsewhere.", HeaderBaseline(source), proposal.SourceCurrent);
                    sceneTargets.Add(target);
                }
            }
            if (changed.Count == 0) return new(V2RoomHeaderCommandStatus.Unchanged);
            ApplyHeader(source, proposal.SourceCurrent, changed);
            source.UpdatedUtc = NextUpdatedUtc(source.UpdatedUtc);
            if (updateReferences)
            {
                foreach (var target in transitionTargets) { target.DestinationRoomReferenceText = proposal.SourceCurrent.ReferenceId; target.UpdatedUtc = NextUpdatedUtc(target.UpdatedUtc); }
                foreach (var target in sceneTargets) target.RoomReferenceText = proposal.SourceCurrent.ReferenceId;
            }
            await db.SaveChangesAsync();
            // The source reference's scoped resolver owns resolver metadata only;
            // captured authored fields were already changed in this transaction.
            var resolverStart = System.Diagnostics.Stopwatch.StartNew();
            await new LogicReferenceResolver(db).ResolveScopedAsync(source, new HashSet<string>(StringComparer.Ordinal) { nameof(Room.ReferenceId) },
                new Dictionary<string, string?>(StringComparer.Ordinal) { [nameof(Room.ReferenceId)] = proposal.SourceBaseline.ReferenceId });
            resolverStart.Stop();
            var mapResolutionAfter = await db.MapScenes.AsNoTracking()
                .Select(x => new { x.Id, x.ResolvedRoomId }).ToListAsync();
            var mapResolutionChanged = mapResolutionAfter.Any(x =>
                !mapResolutionBefore.TryGetValue(x.Id, out var before) || before != x.ResolvedRoomId);
            await tx.CommitAsync();
            return new(V2RoomHeaderCommandStatus.Committed, ResolverElapsed: resolverStart.Elapsed,
                MapResolutionChanged: mapResolutionChanged);
        }
        catch (Exception ex) { return new(V2RoomHeaderCommandStatus.Unexpected, ex.Message, null, proposal.SourceCurrent); }
    }
    public V2RoomHeaderCommandOutcome RevertRoomReferenceRename(RoomReferenceRenameProposal proposal) => new(V2RoomHeaderCommandStatus.Unchanged, RetainedDraft: new(proposal.SourceBaseline.FriendlyName, proposal.SourceBaseline.InGameId, proposal.SourceBaseline.Contributors, proposal.SourceBaseline.Comments, proposal.SourceBaseline.ReferenceId));
    public async Task<V2RoomLifecycleCommandOutcome> SetRoomArchiveAsync(Guid roomId, bool archived)
    {
        try
        {
            await catalog.SetArchivedAsync(new Room { Id = roomId }, archived);
            return new(V2RoomLifecycleCommandStatus.Committed);
        }
        catch (InvalidOperationException ex) { return new(V2RoomLifecycleCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2RoomLifecycleCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2RoomLifecycleCommandOutcome> DeleteRoomAsync(Guid roomId)
    {
        try { return new(V2RoomLifecycleCommandStatus.Committed, SceneImageDeleteElapsed: await catalog.DeleteRoomPermanentlyWithSceneImageTimingAsync(roomId)); }
        catch (InvalidOperationException ex) { return new(V2RoomLifecycleCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2RoomLifecycleCommandStatus.Unexpected, ex.Message); }
    }
    public static RoomHeaderDurableBaseline HeaderFromView(RoomHeaderView x) => new(x.RoomId, x.UpdatedUtc, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.ReferenceId);
    private static readonly string[] HeaderFields = [nameof(Room.FriendlyName), nameof(Room.InGameId), nameof(Room.Contributors), nameof(Room.Comments), nameof(Room.ReferenceId)];
    private static RoomHeaderDurableBaseline HeaderBaseline(Room x) => new(x.Id, x.UpdatedUtc, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.ReferenceId);
    private static HashSet<string> HeaderDifferences(RoomHeaderDurableBaseline x, RoomHeaderDraft d)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (x.FriendlyName != d.FriendlyName) result.Add(nameof(Room.FriendlyName)); if (x.InGameId != d.InGameId) result.Add(nameof(Room.InGameId));
        if (x.Contributors != d.Contributors) result.Add(nameof(Room.Contributors)); if (x.Comments != d.Comments) result.Add(nameof(Room.Comments)); if (x.ReferenceId != d.ReferenceId) result.Add(nameof(Room.ReferenceId)); return result;
    }
    private static string? HeaderValue(RoomHeaderDurableBaseline x, string field) => field switch { nameof(Room.FriendlyName) => x.FriendlyName, nameof(Room.InGameId) => x.InGameId, nameof(Room.Contributors) => x.Contributors, nameof(Room.ReferenceId) => x.ReferenceId, _ => x.Comments };
    private static string? HeaderValue(RoomHeaderDraft x, string field) => field switch { nameof(Room.FriendlyName) => x.FriendlyName, nameof(Room.InGameId) => x.InGameId, nameof(Room.Contributors) => x.Contributors, nameof(Room.ReferenceId) => x.ReferenceId, _ => x.Comments };
    private static string? HeaderValue(Room x, string field) => field switch { nameof(Room.FriendlyName) => x.FriendlyName, nameof(Room.InGameId) => x.InGameId, nameof(Room.Contributors) => x.Contributors, nameof(Room.ReferenceId) => x.ReferenceId, _ => x.Comments };
    private static void ApplyHeader(Room room, RoomHeaderDraft draft, IEnumerable<string> fields) { foreach (var field in fields) switch (field) { case nameof(Room.FriendlyName): room.FriendlyName = draft.FriendlyName; break; case nameof(Room.InGameId): room.InGameId = draft.InGameId; break; case nameof(Room.Contributors): room.Contributors = draft.Contributors; break; case nameof(Room.Comments): room.Comments = draft.Comments; break; case nameof(Room.ReferenceId): room.ReferenceId = draft.ReferenceId; break; } }
    private static DateTime NextUpdatedUtc(DateTime current) { var now = DateTime.UtcNow; return now <= current ? current.AddTicks(1) : now; }
    public async Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft)
    {
        if (draft.ReferenceId != baseline.ReferenceId) return await PrepareSubroomReferenceRenameAsync(roomId, baseline, draft);
        if (GeometryChanged(baseline, draft))
            throw new InvalidOperationException("V2 Phase 2a does not permit subroom geometry persistence.");
        try
        {
            var patch = await catalog.SaveSubroomWithPatchAsync(ToEntity(baseline), ToEntity(baseline, draft), roomId);
            return patch.Status switch
            {
                ChildRowSaveStatus.Committed => new(V2SubroomCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Unchanged => new(V2SubroomCommandStatus.Unchanged, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Missing => new(V2SubroomCommandStatus.Missing, "This subroom no longer exists.", null, draft, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Conflict => new(V2SubroomCommandStatus.Conflict, "This subroom changed elsewhere.", FromEntity(patch.SavedRow!), draft, ResolverElapsed: patch.Outcome.ResolverElapsed),
                _ => new(V2SubroomCommandStatus.Unexpected)
            };
        }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message, null, draft); }
    }

    public async Task<V2SubroomCommandOutcome> PrepareSubroomReferenceRenameAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft)
    {
        if (draft.ReferenceId == baseline.ReferenceId) return await SaveSubroomAsync(roomId, baseline, draft);
        if (GeometryChanged(baseline, draft)) throw new InvalidOperationException("V2 Phase 2a does not permit subroom geometry persistence.");
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 subroom reference proposal requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var source = await db.Subrooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.EntityId && x.RoomId == roomId && !x.IsArchived);
            if (source is null) return new(V2SubroomCommandStatus.Missing, "This subroom no longer exists.", null, draft);
            if (source.UpdatedUtc != baseline.UpdatedUtc && SubroomChanges(baseline, draft).Overlaps(SubroomChanges(baseline, FromEntity(source)))) return new(V2SubroomCommandStatus.Conflict, "This subroom changed elsewhere.", FromEntity(source), draft);
            var transitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSourceSubroomId == source.Id).Select(x => new SubroomReferenceTransitionTargetEvidence(x.Id, x.UpdatedUtc, x.SourceSubroomReferenceText)).ToListAsync();
            var connectionRows = await db.SubroomConnections.AsNoTracking().Where(x => !x.IsArchived && (x.ResolvedSourceSubroomId == source.Id || x.ResolvedDestinationSubroomId == source.Id)).Select(x => new { x.Id, x.UpdatedUtc, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText }).ToListAsync();
            var connections = connectionRows.SelectMany(x => new[] { x.ResolvedSourceSubroomId == source.Id ? new SubroomReferenceConnectionTargetEvidence(x.Id, x.UpdatedUtc, nameof(SubroomConnection.SourceSubroomReferenceText), x.SourceSubroomReferenceText) : null, x.ResolvedDestinationSubroomId == source.Id ? new SubroomReferenceConnectionTargetEvidence(x.Id, x.UpdatedUtc, nameof(SubroomConnection.DestinationSubroomReferenceText), x.DestinationSubroomReferenceText) : null }).Where(x => x is not null).Select(x => x!).ToArray();
            var checks = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSubroomId == source.Id).Select(x => new SubroomReferenceCheckTargetEvidence(x.Id, x.UpdatedUtc, x.SubroomReferenceText)).ToListAsync();
            return new(V2SubroomCommandStatus.Proposal, Proposal: new(baseline, draft, transitions, connections, checks));
        }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public async Task<V2SubroomCommandOutcome> ApplySubroomReferenceRenameAsync(Guid roomId, SubroomReferenceRenameProposal proposal, bool updateReferences)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 subroom reference proposal requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
            var source = await db.Subrooms.SingleOrDefaultAsync(x => x.Id == proposal.SourceBaseline.EntityId && x.RoomId == roomId && !x.IsArchived);
            if (source is null) return new(V2SubroomCommandStatus.Missing, "This subroom no longer exists.", null, proposal.SourceCurrent);
            var changes = SubroomChanges(proposal.SourceBaseline, proposal.SourceCurrent);
            if (source.UpdatedUtc != proposal.SourceBaseline.UpdatedUtc && changes.Overlaps(SubroomChanges(proposal.SourceBaseline, FromEntity(source)))) return new(V2SubroomCommandStatus.Conflict, "This subroom changed elsewhere.", FromEntity(source), proposal.SourceCurrent);
            var transitions = new List<RoomTransition>(); var connections = new Dictionary<Guid, SubroomConnection>(); var checks = new List<CheckLocation>();
            if (updateReferences)
            {
                foreach (var e in proposal.TransitionTargets) { var t = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == e.EntityId); if (t is null || t.IsArchived || t.UpdatedUtc != e.UpdatedUtc || t.SourceSubroomReferenceText != e.ExpectedText) return TargetConflict(source, proposal); transitions.Add(t); }
                foreach (var e in proposal.ConnectionTargets) { var t = await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == e.EntityId); var actual = t is null ? null : e.FieldName == nameof(SubroomConnection.SourceSubroomReferenceText) ? t.SourceSubroomReferenceText : t.DestinationSubroomReferenceText; if (t is null || t.IsArchived || t.UpdatedUtc != e.UpdatedUtc || actual != e.ExpectedText) return TargetConflict(source, proposal); connections[t.Id] = t; }
                foreach (var e in proposal.CheckTargets) { var t = await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == e.EntityId); if (t is null || t.IsArchived || t.UpdatedUtc != e.UpdatedUtc || t.SubroomReferenceText != e.ExpectedText) return TargetConflict(source, proposal); checks.Add(t); }
            }
            if (changes.Count == 0) return new(V2SubroomCommandStatus.Unchanged);
            ApplySubroom(source, proposal.SourceCurrent, changes); source.UpdatedUtc = NextUpdatedUtc(source.UpdatedUtc);
            if (updateReferences) { foreach (var t in transitions) { t.SourceSubroomReferenceText = proposal.SourceCurrent.ReferenceId; t.UpdatedUtc = NextUpdatedUtc(t.UpdatedUtc); } foreach (var e in proposal.ConnectionTargets) { var t = connections[e.EntityId]; if (e.FieldName == nameof(SubroomConnection.SourceSubroomReferenceText)) t.SourceSubroomReferenceText = proposal.SourceCurrent.ReferenceId; else t.DestinationSubroomReferenceText = proposal.SourceCurrent.ReferenceId; } foreach (var t in connections.Values) t.UpdatedUtc = NextUpdatedUtc(t.UpdatedUtc); foreach (var t in checks) { t.SubroomReferenceText = proposal.SourceCurrent.ReferenceId; t.UpdatedUtc = NextUpdatedUtc(t.UpdatedUtc); } }
            await db.SaveChangesAsync(); var watch = System.Diagnostics.Stopwatch.StartNew(); await new LogicReferenceResolver(db).ResolveScopedAsync(source, new HashSet<string>(StringComparer.Ordinal) { nameof(Subroom.ReferenceId) }, new Dictionary<string, string?>(StringComparer.Ordinal) { [nameof(Subroom.ReferenceId)] = proposal.SourceBaseline.ReferenceId }); watch.Stop(); await tx.CommitAsync(); return new(V2SubroomCommandStatus.Committed, ResolverElapsed: watch.Elapsed);
        }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message, null, proposal.SourceCurrent); }
    }
    public V2SubroomCommandOutcome RevertSubroomReferenceRename(SubroomReferenceRenameProposal p) => new(V2SubroomCommandStatus.Unchanged, RetainedDraft: new(p.SourceCurrent.ClientDraftId, p.SourceBaseline.FriendlyName, p.SourceBaseline.ReferenceId, p.SourceBaseline.Notes, p.SourceBaseline.SceneUnitX, p.SourceBaseline.SceneUnitY, p.SourceBaseline.SceneUnitWidth, p.SourceBaseline.SceneUnitHeight));
    private static V2SubroomCommandOutcome TargetConflict(Subroom source, SubroomReferenceRenameProposal p) => new(V2SubroomCommandStatus.Conflict, "A captured reference changed elsewhere.", FromEntity(source), p.SourceCurrent);
    private static HashSet<string> SubroomChanges(SubroomDurableBaseline b, SubroomDraft d) { var r = new HashSet<string>(StringComparer.Ordinal); if (b.FriendlyName != d.FriendlyName) r.Add(nameof(Subroom.FriendlyName)); if (b.ReferenceId != d.ReferenceId) r.Add(nameof(Subroom.ReferenceId)); if (b.Notes != d.Notes) r.Add(nameof(Subroom.Notes)); return r; }
    private static HashSet<string> SubroomChanges(SubroomDurableBaseline b, SubroomDurableBaseline d) { var r = new HashSet<string>(StringComparer.Ordinal); if (b.FriendlyName != d.FriendlyName) r.Add(nameof(Subroom.FriendlyName)); if (b.ReferenceId != d.ReferenceId) r.Add(nameof(Subroom.ReferenceId)); if (b.Notes != d.Notes) r.Add(nameof(Subroom.Notes)); return r; }
    private static void ApplySubroom(Subroom s, SubroomDraft d, IEnumerable<string> fields) { foreach (var f in fields) { if (f == nameof(Subroom.FriendlyName)) s.FriendlyName = d.FriendlyName; else if (f == nameof(Subroom.ReferenceId)) s.ReferenceId = d.ReferenceId; else if (f == nameof(Subroom.Notes)) s.Notes = d.Notes; } }

    public async Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft)
    {
        if (HasGeometry(draft)) throw new InvalidOperationException("V2 Phase 2a does not permit subroom geometry persistence.");
        try
        {
            var created = await catalog.CreateSubroomWithOutcomeAsync(roomId, new Subroom { FriendlyName = draft.FriendlyName, ReferenceId = draft.ReferenceId, Notes = draft.Notes });
            return new(V2SubroomCommandStatus.Committed, CreatedEntityId: created.Entity.Id, ResolverElapsed: created.ResolverElapsed);
        }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message, null, draft); }
    }

    public async Task<V2SubroomCommandOutcome> SaveSubroomGeometryAsync(Guid roomId, SubroomDurableBaseline baseline, double? x, double? y, double? width, double? height)
    {
        var values = new[] { x, y, width, height };
        if (values.Any(v => v is null) && values.Any(v => v is not null))
            return new(V2SubroomCommandStatus.ExpectedFailure, "Scene rectangle values must be all present or all absent.");
        if (values.All(v => v is not null) && (!values.All(v => double.IsFinite(v!.Value)) || width <= 0 || height <= 0))
            return new(V2SubroomCommandStatus.ExpectedFailure, "Scene rectangle requires finite X/Y and positive finite width/height.");
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 subroom geometry command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var durable = await db.Subrooms.AsNoTracking().SingleOrDefaultAsync(row => row.Id == baseline.EntityId && row.RoomId == roomId && !row.IsArchived);
            if (durable is null) return new(V2SubroomCommandStatus.Missing, "This subroom is no longer active in the current room.");
            var fresh = FromEntity(durable);
            var intended = new[] { x, y, width, height };
            var original = new[] { baseline.SceneUnitX, baseline.SceneUnitY, baseline.SceneUnitWidth, baseline.SceneUnitHeight };
            var current = new[] { durable.SceneUnitX, durable.SceneUnitY, durable.SceneUnitWidth, durable.SceneUnitHeight };
            if (durable.UpdatedUtc != baseline.UpdatedUtc && !original.SequenceEqual(current))
                return new(V2SubroomCommandStatus.Conflict, "This subroom geometry changed elsewhere.", fresh,
                    new(Guid.NewGuid(), baseline.FriendlyName, baseline.ReferenceId, baseline.Notes, x, y, width, height));
            if (intended.SequenceEqual(current)) return new(V2SubroomCommandStatus.Unchanged);
            var updated = NextUpdatedUtc(durable.UpdatedUtc);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Subrooms SET SceneUnitX={x}, SceneUnitY={y}, SceneUnitWidth={width}, SceneUnitHeight={height}, UpdatedUtc={updated} WHERE Id={baseline.EntityId} AND RoomId={roomId} AND IsArchived=0 AND UpdatedUtc={durable.UpdatedUtc}");
            if (affected == 0)
            {
                var now = await db.Subrooms.AsNoTracking().SingleOrDefaultAsync(row => row.Id == baseline.EntityId && row.RoomId == roomId);
                return now is null ? new(V2SubroomCommandStatus.Missing, "This subroom no longer exists.") : new(V2SubroomCommandStatus.Conflict, "This subroom changed elsewhere.", FromEntity(now));
            }
            return new(V2SubroomCommandStatus.Committed);
        }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2SubroomCommandOutcome> ClearSubroomAnnotationAsync(Guid roomId, SubroomDurableBaseline baseline)
    {
        var result = await SaveSubroomGeometryAsync(roomId, baseline, null, null, null, null);
        return result.Status == V2SubroomCommandStatus.Committed
            ? result with { AnnotationRefreshImpact = V2AnnotationRefreshImpact.CanvasAndViewport }
            : result;
    }
    public Task<V2SubroomCommandOutcome> ShowSubroomAnnotationAsync(Guid roomId, SubroomDurableBaseline baseline) => SetSubroomAnnotationVisibilityAsync(roomId, baseline, true);
    public Task<V2SubroomCommandOutcome> HideSubroomAnnotationAsync(Guid roomId, SubroomDurableBaseline baseline) => SetSubroomAnnotationVisibilityAsync(roomId, baseline, false);
    private async Task<V2SubroomCommandOutcome> SetSubroomAnnotationVisibilityAsync(Guid roomId, SubroomDurableBaseline baseline, bool enabled)
    {
        try
        {
            if (contexts is null) throw new InvalidOperationException("The V2 subroom annotation command requires a database context factory.");
            await using var db = await contexts.CreateDbContextAsync();
            var durable = await db.Subrooms.AsNoTracking().SingleOrDefaultAsync(row => row.Id == baseline.EntityId && row.RoomId == roomId && !row.IsArchived);
            if (durable is null) return new(V2SubroomCommandStatus.Missing, "This subroom is no longer active in the current room.");
            if (durable.UpdatedUtc != baseline.UpdatedUtc)
                return new(V2SubroomCommandStatus.Conflict, "This subroom changed elsewhere.", FromEntity(durable));
            if (!HasUsableRectangle(durable)) return new(V2SubroomCommandStatus.ExpectedFailure, "This subroom frame cannot be shown or hidden.");
            if (durable.EnableAnnotation == enabled) return new(V2SubroomCommandStatus.Unchanged);
            var updated = NextUpdatedUtc(durable.UpdatedUtc);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Subrooms SET EnableAnnotation={enabled}, UpdatedUtc={updated} WHERE Id={baseline.EntityId} AND RoomId={roomId} AND IsArchived=0 AND UpdatedUtc={durable.UpdatedUtc}");
            if (affected == 0) return new(V2SubroomCommandStatus.Conflict, "This subroom changed elsewhere.");
            return new(V2SubroomCommandStatus.Committed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport);
        }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message); }
    }
    private static bool HasUsableRectangle(Subroom row) => row.SceneUnitX is double x && row.SceneUnitY is double y && row.SceneUnitWidth is double width && row.SceneUnitHeight is double height && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;

    public async Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => await Structural(async () => { await catalog.MoveSubroomAsync(entityId, targetIndex); }, roomId);
    public async Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => await Structural(async () => { await catalog.SetArchivedAsync(new Subroom { Id = entityId }, archived); }, roomId);
    public async Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => await Structural(async () => { await catalog.DeleteSubroomPermanentlyAsync(entityId); }, roomId);

    private static async Task<V2SubroomCommandOutcome> Structural(Func<Task> action, Guid roomId)
    {
        try { await action(); return new(V2SubroomCommandStatus.Committed); }
        catch (InvalidOperationException ex) { return new(V2SubroomCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2SubroomCommandStatus.Unexpected, ex.Message); }
    }
    public static SubroomDurableBaseline FromView(SubroomRowView row) => new(row.EntityId, row.UpdatedUtc, row.SortOrder, row.IsArchived, row.FriendlyName, row.ReferenceId, row.Notes, row.SceneUnitX, row.SceneUnitY, row.SceneUnitWidth, row.SceneUnitHeight, row.EnableAnnotation);
    private static SubroomDurableBaseline FromEntity(Subroom x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.ReferenceId, x.Notes ?? "", x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation);
    private static Subroom ToEntity(SubroomDurableBaseline x, SubroomDraft? d = null) => new() { Id = x.EntityId, UpdatedUtc = x.UpdatedUtc, SortOrder = x.SortOrder, IsArchived = x.IsArchived, FriendlyName = d?.FriendlyName ?? x.FriendlyName, ReferenceId = d?.ReferenceId ?? x.ReferenceId, Notes = d?.Notes ?? x.Notes, SceneUnitX = x.SceneUnitX, SceneUnitY = x.SceneUnitY, SceneUnitWidth = x.SceneUnitWidth, SceneUnitHeight = x.SceneUnitHeight, EnableAnnotation = x.EnableAnnotation };
    private static bool GeometryChanged(SubroomDurableBaseline x, SubroomDraft d) => x.SceneUnitX != d.SceneUnitX || x.SceneUnitY != d.SceneUnitY || x.SceneUnitWidth != d.SceneUnitWidth || x.SceneUnitHeight != d.SceneUnitHeight;
    private static bool HasGeometry(SubroomDraft d) => d.SceneUnitX is not null || d.SceneUnitY is not null || d.SceneUnitWidth is not null || d.SceneUnitHeight is not null;

    public async Task<V2CheckCommandOutcome> SaveCheckAsync(Guid roomId, CheckDurableBaseline baseline, CheckDraft draft)
    {
        try
        {
            var patch = await catalog.SaveCheckWithPatchAsync(ToEntity(baseline), ToEntity(baseline, draft), roomId);
            return patch.Status switch
            {
                ChildRowSaveStatus.Committed => new(V2CheckCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Unchanged => new(V2CheckCommandStatus.Unchanged, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Missing => new(V2CheckCommandStatus.Missing, "This check no longer exists.", null, draft, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Conflict => new(V2CheckCommandStatus.Conflict, "This check changed elsewhere.", FromEntity(patch.SavedRow!), draft, ResolverElapsed: patch.Outcome.ResolverElapsed),
                _ => new(V2CheckCommandStatus.Unexpected)
            };
        }
        catch (Exception ex) { return new(V2CheckCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public async Task<V2CheckCommandOutcome> SaveCheckMetadataAsync(Guid roomId, CheckMetadataDurableBaseline baseline, CheckInGameMetadataDraft draft)
    {
        try
        {
            var patch = await catalog.SaveCheckWithPatchAsync(CheckMetadataEntity(baseline), CheckMetadataEntity(baseline, draft), roomId);
            return patch.Status switch
            {
                ChildRowSaveStatus.Committed => new(V2CheckCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Unchanged => new(V2CheckCommandStatus.Unchanged, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Missing => new(V2CheckCommandStatus.Missing, "This check no longer exists.", null, null, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Conflict => new(V2CheckCommandStatus.Conflict, "This check changed elsewhere.", CheckBaseline(patch.SavedRow!), null, ResolverElapsed: patch.Outcome.ResolverElapsed),
                _ => new(V2CheckCommandStatus.Unexpected)
            };
        }
        catch (Exception ex) { return new(V2CheckCommandStatus.Unexpected, ex.Message); }
    }
    public Task<V2CheckCommandOutcome> PlaceCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline, double x, double y)
    {
        // Placement is the same typed field-diff path as metadata.  The complete
        // baseline retains authored/game fields while this one draft atomically
        // enables the annotation and supplies its complete override pair.
        var draft = new CheckInGameMetadataDraft(baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY,
            baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ, x, y);
        return SaveCheckPlacementAsync(roomId, baseline, draft, true);
    }
    public async Task<V2CheckCommandOutcome> MoveCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline, double x, double y)
    {
        var admission = await AdmitCheckMoveAsync(roomId, baseline.EntityId);
        if (admission is not null) return admission;
        if (!UsablePair(x, y)) return new(V2CheckCommandStatus.ExpectedFailure, "A marker move requires finite coordinates.");
        return await SaveCheckPlacementAsync(roomId, baseline, new(baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY,
            baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ, x, y), true);
    }
    public Task<V2CheckCommandOutcome> RemoveCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline)
    {
        return SaveCheckPlacementAsync(roomId, baseline, new(baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY,
            baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ, baseline.AnnotationSceneUnitX, baseline.AnnotationSceneUnitY), false);
    }
    public async Task<V2CheckCommandOutcome> ShowCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline)
    {
        var durable = await catalog.ReadCheckAsync(baseline.EntityId);
        if (durable is null || durable.RoomId != roomId || durable.IsArchived) return new(V2CheckCommandStatus.Missing, "This check is no longer active in the current room.");
        if (durable.EnableAnnotation) return new(V2CheckCommandStatus.Unchanged);
        if (!UsablePair(durable.AnnotationSceneUnitX, durable.AnnotationSceneUnitY)) return new(V2CheckCommandStatus.ExpectedFailure, "This check annotation cannot be shown.");
        return await SaveCheckPlacementAsync(roomId, baseline, new(baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY,
            baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ,
            baseline.AnnotationSceneUnitX, baseline.AnnotationSceneUnitY), true);
    }
    public Task<V2CheckCommandOutcome> ClearCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline) =>
        SaveCheckPlacementAsync(roomId, baseline, new(baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY, baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ, null, null), baseline.EnableAnnotation);
    public async Task<V2CheckCommandOutcome> ResetCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline)
    {
        var durable = await catalog.ReadCheckAsync(baseline.EntityId);
        if (durable is null || durable.RoomId != roomId || durable.IsArchived) return new(V2CheckCommandStatus.Missing, "This check is no longer active in the current room.");
        if (!UsablePair(durable.InGamePositionX, durable.InGamePositionY)) return new(V2CheckCommandStatus.ExpectedFailure, "This check has no valid imported game position.");
        return await SaveCheckPlacementAsync(roomId, baseline, new(baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY, baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ, durable.InGamePositionX, durable.InGamePositionY), true);
    }
    public async Task<V2CheckCommandOutcome> ShowAndSelectCheckAnnotationAsync(Guid roomId, CheckMetadataDurableBaseline baseline)
    {
        var result = await ShowCheckAnnotationAsync(roomId, baseline);
        return result.Status == V2CheckCommandStatus.Committed ? result with { PostRefreshSelection = new(V2AnnotationSelectionKind.Check, baseline.EntityId) } : result;
    }
    private async Task<V2CheckCommandOutcome?> AdmitCheckMoveAsync(Guid roomId, Guid entityId)
    {
        var durable = await catalog.ReadCheckAsync(entityId);
        if (durable is null || durable.RoomId != roomId || durable.IsArchived) return new(V2CheckCommandStatus.Missing, "This check is no longer active in the current room.");
        if (!durable.EnableAnnotation || !UsablePair(durable.AnnotationSceneUnitX, durable.AnnotationSceneUnitY)) return new(V2CheckCommandStatus.ExpectedFailure, "This check annotation is not positioned and enabled.");
        return null;
    }
    private async Task<V2CheckCommandOutcome> SaveCheckPlacementAsync(Guid roomId, CheckMetadataDurableBaseline baseline, CheckInGameMetadataDraft draft, bool enableAnnotation)
    {
        try
        {
            var enabled = baseline with { EnableAnnotation = enableAnnotation };
            // The original baseline is the durable comparison value.  Only the
            // current entity carries enablement, so the existing typed diff mapper
            // emits one atomic EnableAnnotation + X/Y patch when first placed.
            var patch = await catalog.SaveActiveCheckAnnotationWithPatchAsync(CheckMetadataEntity(baseline), CheckMetadataEntity(enabled, draft), roomId);
            return patch.Status switch
            {
                ChildRowSaveStatus.Committed => new(V2CheckCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport),
                ChildRowSaveStatus.Unchanged => new(V2CheckCommandStatus.Unchanged, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Missing => new(V2CheckCommandStatus.Missing, "This check no longer exists."),
                ChildRowSaveStatus.Conflict => new(V2CheckCommandStatus.Conflict, "This check changed elsewhere.", CheckBaseline(patch.SavedRow!)),
                _ => new(V2CheckCommandStatus.Unexpected)
            };
        }
        catch (Exception ex) { return new(V2CheckCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2CheckCommandOutcome> CreateCheckAsync(Guid roomId, CheckDraft draft)
    {
        try { var created = await catalog.CreateCheckWithOutcomeAsync(roomId, new CheckLocation { FriendlyName=draft.FriendlyName, SubroomReferenceText=draft.SubroomReferenceText, Requirements=draft.Requirements, Notes=draft.Notes, LocationType=draft.LocationType, IsTodo=draft.IsTodo, IsVerified=draft.IsVerified }); return new(V2CheckCommandStatus.Committed, CreatedEntityId: created.Entity.Id, ResolverElapsed: created.ResolverElapsed); }
        catch (Exception ex) { return new(V2CheckCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public async Task<V2CheckCommandOutcome> ReorderCheckAsync(Guid roomId, Guid entityId, int targetIndex) => await CheckStructural(async () => await catalog.MoveCheckAsync(entityId, targetIndex));
    public async Task<V2CheckCommandOutcome> SetCheckArchiveAsync(Guid roomId, Guid entityId, bool archived) => await CheckStructural(async () => await catalog.SetArchivedAsync(new CheckLocation { Id=entityId }, archived));
    public async Task<V2CheckCommandOutcome> DeleteCheckAsync(Guid roomId, Guid entityId) => await CheckStructural(async () => await catalog.DeleteCheckPermanentlyAsync(entityId));
    private static async Task<V2CheckCommandOutcome> CheckStructural(Func<Task> action) { try { await action(); return new(V2CheckCommandStatus.Committed); } catch (InvalidOperationException ex) { return new(V2CheckCommandStatus.ExpectedFailure, ex.Message); } catch (Exception ex) { return new(V2CheckCommandStatus.Unexpected, ex.Message); } }
    public static CheckDurableBaseline CheckFromView(CheckRowView x) => new(x.EntityId,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.RequirementsParseSucceeded,x.FriendlyName,x.SubroomReferenceText,x.Requirements,x.Notes,x.LocationType,x.IsTodo,x.IsVerified);
    public static CheckMetadataDurableBaseline CheckMetadataFromView(CheckRowView x) => new(x.EntityId, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.RequirementsParseSucceeded, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.LocationType, x.EnableAnnotation, x.IsTodo, x.IsVerified, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static CheckDurableBaseline FromEntity(CheckLocation x) => new(x.Id,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.RequirementsParseSucceeded,x.FriendlyName,x.SubroomReferenceText,x.Requirements,x.Notes,x.LocationType,x.IsTodo,x.IsVerified);
    // A nullable authored/reference value and Unknown verification are deliberate
    // draft values, not requests to retain the durable value.
    private static CheckLocation ToEntity(CheckDurableBaseline x, CheckDraft? d = null) => new() { Id=x.EntityId,UpdatedUtc=x.UpdatedUtc,SortOrder=x.SortOrder,IsArchived=x.IsArchived,FriendlyName=d is null?x.FriendlyName:d.FriendlyName,SubroomReferenceText=d is null?x.SubroomReferenceText:d.SubroomReferenceText,Requirements=d is null?x.Requirements:d.Requirements,RequirementsParseSucceeded=x.RequirementsParseSucceeded,Notes=d is null?x.Notes:d.Notes,LocationType=d is null?x.LocationType:d.LocationType,IsTodo=d is null?x.IsTodo:d.IsTodo,IsVerified=d is null?x.IsVerified:d.IsVerified };
    private static CheckDurableBaseline CheckBaseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.RequirementsParseSucceeded, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.LocationType, x.IsTodo, x.IsVerified);
    private static CheckLocation CheckMetadataEntity(CheckMetadataDurableBaseline x, CheckInGameMetadataDraft? d = null) => new()
    {
        Id = x.EntityId, UpdatedUtc = x.UpdatedUtc, SortOrder = x.SortOrder, IsArchived = x.IsArchived,
        FriendlyName = x.FriendlyName, SubroomReferenceText = x.SubroomReferenceText, Requirements = x.Requirements,
        RequirementsParseSucceeded = x.RequirementsParseSucceeded, Notes = x.Notes,
        LocationType = x.LocationType, EnableAnnotation = x.EnableAnnotation, IsTodo = x.IsTodo, IsVerified = x.IsVerified,
        InGameId = d is null ? x.InGameId : d.InGameId, InGamePositionX = d is null ? x.InGamePositionX : d.InGamePositionX,
        InGamePositionY = d is null ? x.InGamePositionY : d.InGamePositionY, InGamePositionZ = d is null ? x.InGamePositionZ : d.InGamePositionZ,
        LocalPositionX = d is null ? x.LocalPositionX : d.LocalPositionX, LocalPositionY = d is null ? x.LocalPositionY : d.LocalPositionY,
        LocalPositionZ = d is null ? x.LocalPositionZ : d.LocalPositionZ, AnnotationSceneUnitX = d is null ? x.AnnotationSceneUnitX : d.AnnotationSceneUnitX,
        AnnotationSceneUnitY = d is null ? x.AnnotationSceneUnitY : d.AnnotationSceneUnitY
    };

    public async Task<V2TransitionCommandOutcome> SaveTransitionAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft)
    {
        try
        {
            var durable = await catalog.ReadTransitionAsync(baseline.EntityId);
            if (durable is null || durable.RoomId != roomId || durable.IsArchived)
                return new(V2TransitionCommandStatus.Missing, "This transition is no longer active in the current room.", null, draft);
            var patch = await catalog.SaveTransitionWithPatchAsync(TransitionEntity(baseline), TransitionEntity(baseline, draft), roomId); return patch.Status switch { ChildRowSaveStatus.Committed => new(V2TransitionCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed), ChildRowSaveStatus.Unchanged => new(V2TransitionCommandStatus.Unchanged, ResolverElapsed: patch.Outcome.ResolverElapsed), ChildRowSaveStatus.Missing => new(V2TransitionCommandStatus.Missing, "This transition no longer exists.", null, draft), ChildRowSaveStatus.Conflict => new(V2TransitionCommandStatus.Conflict, "This transition changed elsewhere.", TransitionBaseline(patch.SavedRow!), draft), _ => new(V2TransitionCommandStatus.Unexpected) };
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public Task<V2TransitionCommandOutcome> SaveTransitionMetadataAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionInGameMetadataDraft draft)
        => SaveTransitionAsync(roomId, baseline, TransitionDraftWithMetadata(baseline, draft));
    public Task<V2TransitionCommandOutcome> PlaceTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline, double x, double y)
        // Placement is a metadata field-diff save: it changes only the complete
        // manual override pair and retains all authored and imported game fields.
        => SaveTransitionAnnotationAsync(roomId, baseline, x, y, true);
    public async Task<V2TransitionCommandOutcome> MoveTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline, double x, double y)
    {
        var admission = await AdmitTransitionMoveAsync(roomId, baseline.EntityId);
        if (admission is not null) return admission;
        if (!UsablePair(x, y)) return new(V2TransitionCommandStatus.ExpectedFailure, "A marker move requires finite coordinates.");
        return await SaveTransitionAnnotationAsync(roomId, baseline, x, y, true);
    }
    public Task<V2TransitionCommandOutcome> RemoveTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline)
        => SaveTransitionAnnotationAsync(roomId, baseline, baseline.AnnotationSceneUnitX, baseline.AnnotationSceneUnitY, false);
    public async Task<V2TransitionCommandOutcome> ShowTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline)
    {
        var durable = await catalog.ReadTransitionAsync(baseline.EntityId);
        if (durable is null || durable.RoomId != roomId || durable.IsArchived) return new(V2TransitionCommandStatus.Missing, "This transition is no longer active in the current room.");
        if (durable.EnableAnnotation) return new(V2TransitionCommandStatus.Unchanged);
        if (!UsablePair(durable.AnnotationSceneUnitX, durable.AnnotationSceneUnitY)) return new(V2TransitionCommandStatus.ExpectedFailure, "This transition annotation cannot be shown.");
        return await SaveTransitionAnnotationAsync(roomId, baseline, baseline.AnnotationSceneUnitX, baseline.AnnotationSceneUnitY, true);
    }
    public Task<V2TransitionCommandOutcome> ClearTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline)
        => SaveTransitionAnnotationAsync(roomId, baseline, null, null, baseline.EnableAnnotation);
    public async Task<V2TransitionCommandOutcome> ResetTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline)
    {
        var durable = await catalog.ReadTransitionAsync(baseline.EntityId);
        if (durable is null || durable.RoomId != roomId || durable.IsArchived) return new(V2TransitionCommandStatus.Missing, "This transition is no longer active in the current room.");
        if (!UsablePair(durable.InGamePositionX, durable.InGamePositionY)) return new(V2TransitionCommandStatus.ExpectedFailure, "This transition has no valid imported game position.");
        return await SaveTransitionAnnotationAsync(roomId, baseline, durable.InGamePositionX, durable.InGamePositionY, true);
    }
    public async Task<V2TransitionCommandOutcome> ShowAndSelectTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline)
    {
        var result = await ShowTransitionAnnotationAsync(roomId, baseline);
        return result.Status == V2TransitionCommandStatus.Committed ? result with { PostRefreshSelection = new(V2AnnotationSelectionKind.Transition, baseline.EntityId) } : result;
    }
    private async Task<V2TransitionCommandOutcome?> AdmitTransitionMoveAsync(Guid roomId, Guid entityId)
    {
        var durable = await catalog.ReadTransitionAsync(entityId);
        if (durable is null || durable.RoomId != roomId || durable.IsArchived) return new(V2TransitionCommandStatus.Missing, "This transition is no longer active in the current room.");
        if (!durable.EnableAnnotation || !UsablePair(durable.AnnotationSceneUnitX, durable.AnnotationSceneUnitY)) return new(V2TransitionCommandStatus.ExpectedFailure, "This transition annotation is not positioned and enabled.");
        return null;
    }
    private static bool UsablePair(double? x, double? y) => x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b);
    private async Task<V2TransitionCommandOutcome> SaveTransitionAnnotationAsync(Guid roomId, TransitionDurableBaseline baseline, double? x, double? y, bool enabled)
    {
        try
        {
            var current = TransitionEntity(baseline);
            current.EnableAnnotation = enabled;
            current.AnnotationSceneUnitX = x;
            current.AnnotationSceneUnitY = y;
            var patch = await catalog.SaveTransitionWithPatchAsync(TransitionEntity(baseline), current, roomId);
            return patch.Status switch
            {
                ChildRowSaveStatus.Committed => new(V2TransitionCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed, AnnotationRefreshImpact: V2AnnotationRefreshImpact.CanvasAndViewport),
                ChildRowSaveStatus.Unchanged => new(V2TransitionCommandStatus.Unchanged),
                ChildRowSaveStatus.Missing => new(V2TransitionCommandStatus.Missing, "This transition no longer exists."),
                ChildRowSaveStatus.Conflict => new(V2TransitionCommandStatus.Conflict, "This transition changed elsewhere.", TransitionBaseline(patch.SavedRow!)),
                _ => new(V2TransitionCommandStatus.Unexpected)
            };
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2TransitionCommandOutcome> CreateTransitionAsync(Guid roomId, TransitionDraft draft)
        => await PrepareCreateTransitionInverseAsync(roomId, draft);

    public async Task<V2TransitionCommandOutcome> PrepareCreateTransitionInverseAsync(Guid roomId, TransitionDraft draft)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, draft);
        try
        {
            var candidate = await FindInverseCandidateAsync(roomId, draft);
            if (candidate is null)
            {
                var created = await catalog.CreateTransitionWithOutcomeAsync(roomId, TransitionEntity(null, draft));
                return new(V2TransitionCommandStatus.Committed, CreatedEntityId: created.Entity.Id, ResolverElapsed: created.ResolverElapsed);
            }
            var inverse = candidate.Value;
            return new(V2TransitionCommandStatus.Proposal, CreateProposal: new(draft, inverse.Target,
                inverse.SourceRoomReferenceId, draft.Alias, draft.FriendlyName, inverse.TargetFriendlyName));
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    public async Task<V2TransitionCommandOutcome> ReorderTransitionAsync(Guid roomId, Guid entityId, int targetIndex) => await TransitionStructural(roomId, entityId, () => catalog.MoveTransitionAsync(entityId, targetIndex));
    public async Task<V2TransitionCommandOutcome> SetTransitionArchiveAsync(Guid roomId, Guid entityId, bool archived) => await TransitionStructural(roomId, entityId, () => catalog.SetArchivedAsync(new RoomTransition { Id = entityId }, archived));
    public async Task<V2TransitionCommandOutcome> DeleteTransitionAsync(Guid roomId, Guid entityId) => await TransitionStructural(roomId, entityId, () => catalog.DeleteTransitionPermanentlyAsync(entityId));
    private async Task<V2TransitionCommandOutcome> TransitionStructural(Guid roomId, Guid entityId, Func<Task> action) { try { if (contexts is not null) { await using var db = await contexts.CreateDbContextAsync(); if (!await db.RoomTransitions.AsNoTracking().AnyAsync(x => x.Id == entityId && x.RoomId == roomId)) return new(V2TransitionCommandStatus.Missing, "This transition no longer exists."); } await action(); return new(V2TransitionCommandStatus.Committed); } catch (InvalidOperationException ex) { return new(V2TransitionCommandStatus.ExpectedFailure, ex.Message); } catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message); } }
    private async Task<V2TransitionCommandOutcome> PrepareInverseLegacyAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, draft);
        try { await using var db = await contexts.CreateDbContextAsync(); var source = await db.RoomTransitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.EntityId && x.RoomId == roomId); if (source is null) return new(V2TransitionCommandStatus.Missing, "This transition no longer exists.", null, draft); var proposed = TransitionEntity(baseline, draft); proposed.RoomId = source.RoomId; proposed.ResolvedDestinationRoomId = source.ResolvedDestinationRoomId; proposed.ResolvedDestinationTransitionId = source.ResolvedDestinationTransitionId; var rooms = await db.Rooms.AsNoTracking().ToListAsync(); var transitions = await db.RoomTransitions.AsNoTracking().ToListAsync(); var candidate = TransitionInverseSetupService.CreateDraft(proposed, rooms, transitions); if (candidate is null) return await SaveTransitionAsync(roomId, baseline, draft); var target = transitions.Single(x => x.Id == candidate.TargetTransitionId); var evidence = new TransitionInverseTargetEvidence(target.Id, target.UpdatedUtc, target.DestinationRoomReferenceText, target.DestinationTransitionAliasText, candidate.FillDestinationRoomReference, candidate.FillDestinationTransitionAlias); return new(V2TransitionCommandStatus.Proposal, Proposal: new(baseline, draft, evidence, candidate.SourceRoomReferenceId, candidate.SourceTransitionAlias, candidate.SourceTransitionFriendlyName, candidate.TargetTransitionFriendlyName)); }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, draft); }
    }
    private async Task<V2TransitionCommandOutcome> ApplyInverseLegacyAsync(Guid roomId, TransitionInverseProposal proposal, bool updateInverse)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, proposal.SourceCurrent);
        try { await using var db = await contexts.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync(); var source = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == proposal.SourceBaseline.EntityId && x.RoomId == roomId); if (source is null) return new(V2TransitionCommandStatus.Missing, "This transition no longer exists.", null, proposal.SourceCurrent); var baselineEntity = TransitionEntity(proposal.SourceBaseline); var currentEntity = TransitionEntity(proposal.SourceBaseline, proposal.SourceCurrent); var changed = TransitionFieldDiffMapper.Differences(baselineEntity, currentEntity); if (source.UpdatedUtc != proposal.SourceBaseline.UpdatedUtc && TransitionFieldDiffMapper.Differences(baselineEntity, source).Intersect(changed).Any()) return new(V2TransitionCommandStatus.Conflict, "This transition changed elsewhere.", TransitionBaseline(source), proposal.SourceCurrent); RoomTransition? target = null; if (updateInverse) { target = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == proposal.Target.TargetTransitionId); if (target is null || target.UpdatedUtc != proposal.Target.UpdatedUtc || target.DestinationRoomReferenceText != proposal.Target.ExpectedDestinationRoomReferenceText || target.DestinationTransitionAliasText != proposal.Target.ExpectedDestinationTransitionAliasText) return new(V2TransitionCommandStatus.Conflict, "The inverse transition changed elsewhere.", TransitionBaseline(source), proposal.SourceCurrent); } if (changed.Count > 0) { TransitionFieldDiffMapper.Apply(source, currentEntity, changed); source.UpdatedUtc = DateTime.UtcNow; } if (target is not null) { if (proposal.Target.FillDestinationRoomReferenceText && string.IsNullOrWhiteSpace(target.DestinationRoomReferenceText)) target.DestinationRoomReferenceText = proposal.SourceRoomReferenceId; if (proposal.Target.FillDestinationTransitionAliasText && string.IsNullOrWhiteSpace(target.DestinationTransitionAliasText)) target.DestinationTransitionAliasText = proposal.SourceAlias; } await db.SaveChangesAsync(); var timer = System.Diagnostics.Stopwatch.StartNew(); await new LogicReferenceResolver(db).ResolveAsync(); timer.Stop(); await tx.CommitAsync(); return new(V2TransitionCommandStatus.Committed, ResolverElapsed: timer.Elapsed); }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, proposal.SourceCurrent); }
    }
    public async Task<V2TransitionCommandOutcome> PrepareInverseAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, draft);
        try
        {
            // Inverse setup is tied only to a destination edit, never an unrelated save.
            if (baseline.DestinationRoomReferenceText == draft.DestinationRoomReferenceText && baseline.DestinationTransitionAliasText == draft.DestinationTransitionAliasText)
                return await SaveTransitionAsync(roomId, baseline, draft);
            if (string.IsNullOrWhiteSpace(draft.DestinationRoomReferenceText) || string.IsNullOrWhiteSpace(draft.DestinationTransitionAliasText) || string.IsNullOrWhiteSpace(draft.Alias))
                return await SaveTransitionAsync(roomId, baseline, draft);

            await using var db = await contexts.CreateDbContextAsync();
            var source = await db.RoomTransitions.AsNoTracking()
                .Where(x => x.Id == baseline.EntityId && x.RoomId == roomId && !x.IsArchived)
                .Join(db.Rooms.AsNoTracking().Where(x => !x.IsArchived), transition => transition.RoomId, room => room.Id,
                    (transition, room) => new { transition.Id, transition.RoomId, room.ReferenceId })
                .SingleOrDefaultAsync();
            if (source is null) return new(V2TransitionCommandStatus.Missing, "This transition no longer exists.", null, draft);

            var destinationRooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived && x.ReferenceId.Trim().ToLower() == draft.DestinationRoomReferenceText.Trim().ToLower()).Select(x => x.Id).ToListAsync();
            if (destinationRooms.Count != 1) return await SaveTransitionAsync(roomId, baseline, draft);
            var targets = await db.RoomTransitions.AsNoTracking()
                .Where(x => !x.IsArchived && x.RoomId == destinationRooms[0] && x.Alias.Trim().ToLower() == draft.DestinationTransitionAliasText.Trim().ToLower())
                .Select(x => new { x.Id, x.FriendlyName, x.UpdatedUtc, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText })
                .ToListAsync();
            if (targets.Count != 1) return await SaveTransitionAsync(roomId, baseline, draft);

            var matchingSourceRooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived && x.ReferenceId.Trim().ToLower() == source.ReferenceId.Trim().ToLower()).Select(x => x.Id).ToListAsync();
            if (matchingSourceRooms.Count != 1 || matchingSourceRooms[0] != source.RoomId) return await SaveTransitionAsync(roomId, baseline, draft);
            var aliases = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.RoomId == roomId).Select(x => new { x.Id, x.Alias }).ToListAsync();
            if (aliases.Count(x => string.Equals(x.Id == source.Id ? draft.Alias.Trim() : x.Alias.Trim(), draft.Alias.Trim(), StringComparison.OrdinalIgnoreCase)) != 1)
                return await SaveTransitionAsync(roomId, baseline, draft);

            var target = targets[0];
            var fillRoom = string.IsNullOrWhiteSpace(target.DestinationRoomReferenceText);
            var fillAlias = string.IsNullOrWhiteSpace(target.DestinationTransitionAliasText);
            if (!fillRoom && !fillAlias) return await SaveTransitionAsync(roomId, baseline, draft);
            return new(V2TransitionCommandStatus.Proposal, Proposal: new(baseline, draft,
                new(target.Id, target.UpdatedUtc, target.DestinationRoomReferenceText, target.DestinationTransitionAliasText, fillRoom, fillAlias),
                source.ReferenceId, draft.Alias, draft.FriendlyName, target.FriendlyName));
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, draft); }
    }

    public async Task<V2TransitionCommandOutcome> PrepareInverseStatusActionAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, draft);
        try
        {
            await using var db = await contexts.CreateDbContextAsync();
            var source = await db.RoomTransitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.EntityId && x.RoomId == roomId);
            if (source is null || source.IsArchived) return new(V2TransitionCommandStatus.Missing, "This transition is no longer active in the current room.", null, draft);

            // Status preparation intentionally has the same eligibility as ordinary
            // inverse setup. In particular, its source room reference and alias must
            // still be unique active authored values at click time.
            var rooms = await db.Rooms.AsNoTracking().ToListAsync();
            var transitions = await db.RoomTransitions.AsNoTracking().ToListAsync();
            var candidate = TransitionInverseSetupService.CreateDraft(source, rooms, transitions);
            if (candidate is null) return new(V2TransitionCommandStatus.ExpectedFailure, "This inverse setup is no longer applicable.", null, draft);

            var target = transitions.Single(x => x.Id == candidate.TargetTransitionId);
            return new(V2TransitionCommandStatus.Proposal, Proposal: new(baseline, draft,
                new(target.Id, target.UpdatedUtc, target.DestinationRoomReferenceText, target.DestinationTransitionAliasText,
                    candidate.FillDestinationRoomReference, candidate.FillDestinationTransitionAlias),
                candidate.SourceRoomReferenceId, candidate.SourceTransitionAlias, candidate.SourceTransitionFriendlyName,
                candidate.TargetTransitionFriendlyName, true));
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, draft); }
    }

    public async Task<V2TransitionCommandOutcome> ApplyInverseAsync(Guid roomId, TransitionInverseProposal proposal, bool updateInverse)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, proposal.SourceCurrent);
        try
        {
            await using var db = await contexts.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var source = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == proposal.SourceBaseline.EntityId && x.RoomId == roomId && !x.IsArchived);
            if (source is null) return new(V2TransitionCommandStatus.Missing, "This transition no longer exists.", null, proposal.SourceCurrent);
            var baseline = TransitionEntity(proposal.SourceBaseline);
            var current = TransitionEntity(proposal.SourceBaseline, proposal.SourceCurrent);
            var sourceChanges = proposal.IsTargetOnlyStatusAction ? [] : TransitionFieldDiffMapper.Differences(baseline, current);
            if (source.UpdatedUtc != proposal.SourceBaseline.UpdatedUtc && TransitionFieldDiffMapper.Differences(baseline, source).Intersect(sourceChanges).Any())
                return new(V2TransitionCommandStatus.Conflict, "This transition changed elsewhere.", TransitionBaseline(source), proposal.SourceCurrent);

            RoomTransition? target = null;
            if (updateInverse)
            {
                target = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == proposal.Target.TargetTransitionId && !x.IsArchived);
                if (target is null || target.UpdatedUtc != proposal.Target.UpdatedUtc || target.DestinationRoomReferenceText != proposal.Target.ExpectedDestinationRoomReferenceText || target.DestinationTransitionAliasText != proposal.Target.ExpectedDestinationTransitionAliasText)
                    return new(V2TransitionCommandStatus.Conflict, "The inverse transition changed elsewhere.", TransitionBaseline(source), proposal.SourceCurrent);
                if (proposal.IsTargetOnlyStatusAction && !await StatusActionSourceStillMatchesAsync(db, roomId, source, target, proposal))
                    return new(V2TransitionCommandStatus.Conflict, "This inverse setup changed elsewhere.", TransitionBaseline(source), proposal.SourceCurrent);
            }
            if (sourceChanges.Count == 0 && target is null) return new(V2TransitionCommandStatus.Unchanged);

            var sourceOriginalText = db.Entry(source).Properties.ToDictionary(x => x.Metadata.Name, x => x.OriginalValue as string, StringComparer.Ordinal);
            if (sourceChanges.Count > 0) TransitionFieldDiffMapper.Apply(source, current, sourceChanges);
            var targetChanges = new HashSet<string>(StringComparer.Ordinal);
            if (target is not null)
            {
                if (proposal.Target.FillDestinationRoomReferenceText) { target.DestinationRoomReferenceText = proposal.SourceRoomReferenceId; targetChanges.Add(nameof(RoomTransition.DestinationRoomReferenceText)); }
                if (proposal.Target.FillDestinationTransitionAliasText) { target.DestinationTransitionAliasText = proposal.SourceAlias; targetChanges.Add(nameof(RoomTransition.DestinationTransitionAliasText)); }
            }
            await db.SaveChangesAsync();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (sourceChanges.Count > 0) await new LogicReferenceResolver(db).ResolveScopedAsync(source, sourceChanges.ToHashSet(StringComparer.Ordinal), sourceOriginalText);
            if (target is not null) await new LogicReferenceResolver(db).ResolveScopedAsync(target, targetChanges, new Dictionary<string, string?>(StringComparer.Ordinal));
            stopwatch.Stop();
            if (sourceChanges.Contains(nameof(RoomTransition.Requirements))) await ValidateTransitionRequirementsAsync(db, source);
            await transaction.CommitAsync();
            return new(V2TransitionCommandStatus.Committed, ResolverElapsed: stopwatch.Elapsed);
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, proposal.SourceCurrent); }
    }

    private static async Task<bool> StatusActionSourceStillMatchesAsync(LogicDbContext db, Guid roomId,
        RoomTransition source, RoomTransition target, TransitionInverseProposal proposal)
    {
        // This is deliberately immediately before the target-only write.  The status
        // action has no source save, so its captured active inverse context must still
        // be exact rather than relying on target evidence alone.
        if (source.UpdatedUtc != proposal.SourceBaseline.UpdatedUtc ||
            source.Alias != proposal.SourceAlias ||
            source.DestinationRoomReferenceText != proposal.SourceBaseline.DestinationRoomReferenceText ||
            source.DestinationTransitionAliasText != proposal.SourceBaseline.DestinationTransitionAliasText ||
            source.ResolvedDestinationRoomId != target.RoomId ||
            source.ResolvedDestinationTransitionId != target.Id)
            return false;

        var sourceRoom = await db.Rooms.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived);
        if (sourceRoom is null || sourceRoom.ReferenceId != proposal.SourceRoomReferenceId)
            return false;

        var uniqueSourceRoom = await db.Rooms.CountAsync(x => !x.IsArchived &&
            x.ReferenceId.Trim().ToLower() == sourceRoom.ReferenceId.Trim().ToLower()) == 1;
        return uniqueSourceRoom && await db.RoomTransitions.CountAsync(x => !x.IsArchived &&
            x.RoomId == roomId && x.Alias.Trim().ToLower() == source.Alias.Trim().ToLower()) == 1;
    }

    public async Task<V2TransitionCommandOutcome> ApplyCreateInverseAsync(Guid roomId, TransitionInverseCreateProposal proposal, bool updateInverse)
    {
        if (contexts is null) return new(V2TransitionCommandStatus.Unexpected, "The inverse proposal requires a database context factory.", null, proposal.SourceCurrent);
        try
        {
            await using var db = await contexts.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var sourceRoom = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived);
            if (sourceRoom is null) return new(V2TransitionCommandStatus.Missing, "This room no longer exists.", null, proposal.SourceCurrent);
            RoomTransition? target = null;
            if (updateInverse)
            {
                target = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == proposal.Target.TargetTransitionId && !x.IsArchived);
                if (target is null || target.UpdatedUtc != proposal.Target.UpdatedUtc ||
                    target.DestinationRoomReferenceText != proposal.Target.ExpectedDestinationRoomReferenceText ||
                    target.DestinationTransitionAliasText != proposal.Target.ExpectedDestinationTransitionAliasText)
                    return new(V2TransitionCommandStatus.Conflict, "The inverse transition changed elsewhere.", null, proposal.SourceCurrent);
            }

            var source = TransitionEntity(null, proposal.SourceCurrent);
            source.RoomId = roomId;
            source.SortOrder = await db.RoomTransitions.Where(x => x.RoomId == roomId && !x.IsArchived).Select(x => (int?)x.SortOrder).MaxAsync() is int last ? last + 1 : 0;
            db.RoomTransitions.Add(source);
            if (updateInverse)
            {
                if (proposal.Target.FillDestinationRoomReferenceText) target!.DestinationRoomReferenceText = proposal.SourceRoomReferenceId;
                if (proposal.Target.FillDestinationTransitionAliasText) target!.DestinationTransitionAliasText = proposal.SourceAlias;
            }
            await db.SaveChangesAsync();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await new LogicReferenceResolver(db).ResolveCreatedAsync(source);
            if (target is not null)
            {
                var targetFields = new HashSet<string>(StringComparer.Ordinal);
                if (proposal.Target.FillDestinationRoomReferenceText) targetFields.Add(nameof(RoomTransition.DestinationRoomReferenceText));
                if (proposal.Target.FillDestinationTransitionAliasText) targetFields.Add(nameof(RoomTransition.DestinationTransitionAliasText));
                await new LogicReferenceResolver(db).ResolveScopedAsync(target, targetFields, new Dictionary<string, string?>(StringComparer.Ordinal));
            }
            stopwatch.Stop();
            await ValidateTransitionRequirementsAsync(db, source);
            await transaction.CommitAsync();
            return new(V2TransitionCommandStatus.Committed, CreatedEntityId: source.Id, ResolverElapsed: stopwatch.Elapsed);
        }
        catch (Exception ex) { return new(V2TransitionCommandStatus.Unexpected, ex.Message, null, proposal.SourceCurrent); }
    }

    public V2TransitionCommandOutcome RevertCreateInverse(TransitionInverseCreateProposal proposal)
        => new(V2TransitionCommandStatus.Unchanged, RetainedDraft: proposal.SourceCurrent);

    private async Task ValidateTransitionRequirementsAsync(LogicDbContext db, RoomTransition transition)
    {
        var context = await requirementStatus.LoadContextAsync(db, CancellationToken.None);
        transition.RequirementsParseSucceeded = requirementStatus.ValidateSafely(context, transition.RoomId, transition.Id,
            RequirementBearingRowKind.Transition, transition.Requirements);
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync();
    }

    public V2TransitionCommandOutcome RevertInverse(TransitionInverseProposal proposal)
        => new(V2TransitionCommandStatus.Unchanged, RetainedDraft: proposal.IsTargetOnlyStatusAction ? proposal.SourceCurrent : TransitionDraftFromBaseline(proposal.SourceBaseline, proposal.SourceCurrent.ClientDraftId));

    private static TransitionDraft TransitionDraftFromBaseline(TransitionDurableBaseline x, Guid clientDraftId) => new(clientDraftId, x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified);
    private static TransitionDraft TransitionDraftWithMetadata(TransitionDurableBaseline x, TransitionInGameMetadataDraft d)
        => new(Guid.NewGuid(), x.Alias, x.FriendlyName, d.InGameId, d.InGamePositionX, d.InGamePositionY, d.InGamePositionZ,
            d.LocalPositionX, d.LocalPositionY, d.LocalPositionZ, d.AnnotationSceneUnitX, d.AnnotationSceneUnitY,
            x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified);
    private async Task<(TransitionInverseTargetEvidence Target, string SourceRoomReferenceId, string TargetFriendlyName)?> FindInverseCandidateAsync(Guid roomId, TransitionDraft draft)
    {
        if (contexts is null || string.IsNullOrWhiteSpace(draft.DestinationRoomReferenceText) || string.IsNullOrWhiteSpace(draft.DestinationTransitionAliasText) || string.IsNullOrWhiteSpace(draft.Alias)) return null;
        await using var db = await contexts.CreateDbContextAsync();
        var sourceRooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived && x.Id == roomId).Select(x => new { x.Id, x.ReferenceId }).ToListAsync();
        if (sourceRooms.Count != 1 || string.IsNullOrWhiteSpace(sourceRooms[0].ReferenceId)) return null;
        var sourceReferenceMatches = await db.Rooms.AsNoTracking().CountAsync(x => !x.IsArchived && x.ReferenceId.Trim().ToLower() == sourceRooms[0].ReferenceId.Trim().ToLower());
        if (sourceReferenceMatches != 1) return null;
        var sourceAliasMatches = await db.RoomTransitions.AsNoTracking().CountAsync(x => !x.IsArchived && x.RoomId == roomId && x.Alias.Trim().ToLower() == draft.Alias.Trim().ToLower());
        if (sourceAliasMatches != 0) return null;
        var destinationRooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived && x.ReferenceId.Trim().ToLower() == draft.DestinationRoomReferenceText.Trim().ToLower()).Select(x => x.Id).ToListAsync();
        if (destinationRooms.Count != 1) return null;
        var targets = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.RoomId == destinationRooms[0] && x.Alias.Trim().ToLower() == draft.DestinationTransitionAliasText.Trim().ToLower()).Select(x => new { x.Id, x.FriendlyName, x.UpdatedUtc, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText }).ToListAsync();
        if (targets.Count != 1) return null;
        var target = targets[0]; var fillRoom = string.IsNullOrWhiteSpace(target.DestinationRoomReferenceText); var fillAlias = string.IsNullOrWhiteSpace(target.DestinationTransitionAliasText);
        return !fillRoom && !fillAlias ? null : (new(target.Id, target.UpdatedUtc, target.DestinationRoomReferenceText, target.DestinationTransitionAliasText, fillRoom, fillAlias), sourceRooms[0].ReferenceId, target.FriendlyName);
    }
    public static TransitionDurableBaseline TransitionFromView(TransitionRowView x) => new(x.EntityId,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.RequirementsParseSucceeded,x.Alias,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SourceSubroomReferenceText,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText,x.Requirements,x.Notes,x.IsTodo,x.IsVerified,x.EnableAnnotation);
    private static TransitionDurableBaseline TransitionBaseline(RoomTransition x) => new(x.Id,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.RequirementsParseSucceeded,x.Alias,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SourceSubroomReferenceText,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText,x.Requirements,x.Notes,x.IsTodo,x.IsVerified,x.EnableAnnotation);
    private static RoomTransition TransitionEntity(TransitionDurableBaseline? x, TransitionDraft? d = null) => new() { Id=x?.EntityId ?? Guid.Empty, UpdatedUtc=x?.UpdatedUtc ?? default, SortOrder=x?.SortOrder ?? 0, IsArchived=x?.IsArchived ?? false, EnableAnnotation=x?.EnableAnnotation ?? false, Alias=d is null ? x?.Alias ?? "" : d.Alias, FriendlyName=d is null ? x?.FriendlyName ?? "" : d.FriendlyName, InGameId=d is null ? x?.InGameId : d.InGameId, InGamePositionX=d is null ? x?.InGamePositionX : d.InGamePositionX, InGamePositionY=d is null ? x?.InGamePositionY : d.InGamePositionY, InGamePositionZ=d is null ? x?.InGamePositionZ : d.InGamePositionZ, LocalPositionX=d is null ? x?.LocalPositionX : d.LocalPositionX, LocalPositionY=d is null ? x?.LocalPositionY : d.LocalPositionY, LocalPositionZ=d is null ? x?.LocalPositionZ : d.LocalPositionZ, AnnotationSceneUnitX=d is null ? x?.AnnotationSceneUnitX : d.AnnotationSceneUnitX, AnnotationSceneUnitY=d is null ? x?.AnnotationSceneUnitY : d.AnnotationSceneUnitY, SourceSubroomReferenceText=d is null ? x?.SourceSubroomReferenceText : d.SourceSubroomReferenceText, DestinationRoomReferenceText=d is null ? x?.DestinationRoomReferenceText : d.DestinationRoomReferenceText, DestinationTransitionAliasText=d is null ? x?.DestinationTransitionAliasText : d.DestinationTransitionAliasText, Requirements=d is null ? x?.Requirements ?? "" : d.Requirements, RequirementsParseSucceeded=x?.RequirementsParseSucceeded, Notes=d is null ? x?.Notes ?? "" : d.Notes, IsTodo=d is null ? x?.IsTodo ?? false : d.IsTodo, IsVerified=d is null ? x?.IsVerified : d.IsVerified };

    public async Task<V2ConnectionCommandOutcome> SaveConnectionAsync(Guid roomId, ConnectionDurableBaseline baseline, ConnectionDraft draft)
    {
        try
        {
            // The coordinator owns room serialization and the catalog owns the
            // alias-group annotation reconciliation. V2 must not patch siblings.
            var patch = await connections.SaveAsync(ConnectionEntity(baseline), ConnectionEntity(baseline, draft), roomId);
            return patch.Status switch
            {
                ChildRowSaveStatus.Committed => new(V2ConnectionCommandStatus.Committed, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Unchanged => new(V2ConnectionCommandStatus.Unchanged, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Missing => new(V2ConnectionCommandStatus.Missing, "This connection no longer exists.", null, draft, ResolverElapsed: patch.Outcome.ResolverElapsed),
                ChildRowSaveStatus.Conflict => new(V2ConnectionCommandStatus.Conflict, "This connection changed elsewhere.", ConnectionBaseline(patch.SavedRow!), draft, ResolverElapsed: patch.Outcome.ResolverElapsed),
                _ => new(V2ConnectionCommandStatus.Unexpected)
            };
        }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message, null, draft); }
    }

    public async Task<V2ConnectionCommandOutcome> CreateConnectionAsync(Guid roomId, ConnectionDraft draft)
    {
        try
        {
            var created = await catalog.CreateConnectionWithOutcomeAsync(roomId, ConnectionEntity(null, draft));
            return new(V2ConnectionCommandStatus.Committed, CreatedEntityId: created.Entity.Id, ResolverElapsed: created.ResolverElapsed);
        }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message, null, draft); }
    }

    public async Task<V2ConnectionCommandOutcome> ReorderConnectionAsync(Guid roomId, Guid entityId, int targetIndex)
    {
        // Archived rows are correction records, not an ordering partition.  Admit
        // neither a catalogue move nor its timestamp/sort-order write for them.
        try
        {
            if (contexts is not null)
            {
                await using var db = await contexts.CreateDbContextAsync();
                if (!await db.SubroomConnections.AsNoTracking().AnyAsync(x => x.Id == entityId && x.RoomId == roomId && !x.IsArchived))
                    return new(V2ConnectionCommandStatus.Missing, "This active connection no longer exists.");
            }
            await catalog.MoveConnectionAsync(entityId, targetIndex);
            return new(V2ConnectionCommandStatus.Committed);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public async Task<V2ConnectionCommandOutcome> SetConnectionArchiveAsync(Guid roomId, Guid entityId, bool archived)
        => await ConnectionStructural(roomId, entityId, () => catalog.SetArchivedAsync(new SubroomConnection { Id = entityId }, archived));
    public async Task<V2ConnectionCommandOutcome> DeleteConnectionAsync(Guid roomId, Guid entityId)
        => await ConnectionStructural(roomId, entityId, () => catalog.DeleteConnectionPermanentlyAsync(entityId));
    public async Task<V2ConnectionCommandOutcome> ScaffoldInverseConnectionAsync(Guid roomId, Guid entityId)
    {
        try
        {
            if (contexts is not null)
            {
                await using var db = await contexts.CreateDbContextAsync();
                if (!await db.SubroomConnections.AsNoTracking().AnyAsync(x => x.Id == entityId && x.RoomId == roomId))
                    return new(V2ConnectionCommandStatus.Missing, "This connection no longer exists.");
            }
            var created = await catalog.ScaffoldInverseConnectionWithOutcomeAsync(entityId);
            return new(V2ConnectionCommandStatus.Committed, CreatedEntityId: created.Entity.Id, ResolverElapsed: created.ResolverElapsed);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    private async Task<V2ConnectionCommandOutcome> ConnectionStructural(Guid roomId, Guid entityId, Func<Task> action)
    {
        try
        {
            if (contexts is not null)
            {
                await using var db = await contexts.CreateDbContextAsync();
                if (!await db.SubroomConnections.AsNoTracking().AnyAsync(x => x.Id == entityId && x.RoomId == roomId))
                    return new(V2ConnectionCommandStatus.Missing, "This connection no longer exists.");
            }
            await action();
            return new(V2ConnectionCommandStatus.Committed);
        }
        catch (InvalidOperationException ex) { return new(V2ConnectionCommandStatus.ExpectedFailure, ex.Message); }
        catch (Exception ex) { return new(V2ConnectionCommandStatus.Unexpected, ex.Message); }
    }
    public static ConnectionDurableBaseline ConnectionFromView(ConnectionRowView x) => new(x.EntityId, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.RequirementsParseSucceeded, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.EnableAnnotation, x.DurableSceneUnitX ?? x.SceneUnitX, x.DurableSceneUnitY ?? x.SceneUnitY, x.IsTodo, x.IsVerified);
    private static ConnectionDurableBaseline ConnectionBaseline(SubroomConnection x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.RequirementsParseSucceeded, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY, x.IsTodo, x.IsVerified);
    private static SubroomConnection ConnectionEntity(ConnectionDurableBaseline? x, ConnectionDraft? d = null) => new()
    {
        Id = x?.EntityId ?? Guid.Empty, UpdatedUtc = x?.UpdatedUtc ?? default, SortOrder = x?.SortOrder ?? 0, IsArchived = x?.IsArchived ?? false,
        Alias = d?.Alias ?? x?.Alias ?? "", FriendlyName = d?.FriendlyName ?? x?.FriendlyName ?? "",
        SourceSubroomReferenceText = d?.SourceSubroomReferenceText ?? x?.SourceSubroomReferenceText ?? "",
        DestinationSubroomReferenceText = d?.DestinationSubroomReferenceText ?? x?.DestinationSubroomReferenceText ?? "",
        Requirements = d?.Requirements ?? x?.Requirements ?? "", RequirementsParseSucceeded = x?.RequirementsParseSucceeded,
        Notes = d?.Notes ?? x?.Notes ?? "",
        // Annotation values remain loaded with the V2 row/baseline so an
        // ordinary authored-field save preserves the domain-owned alias-group
        // state. They are deliberately never taken from the Phase-2c draft.
        // A create has no durable baseline and therefore retains entity defaults.
        EnableAnnotation = x?.EnableAnnotation ?? false,
        SceneUnitX = x?.SceneUnitX,
        SceneUnitY = x?.SceneUnitY,
        IsTodo = d is null ? x?.IsTodo ?? false : d.IsTodo,
        IsVerified = d is null ? x?.IsVerified : d.IsVerified
    };
}
