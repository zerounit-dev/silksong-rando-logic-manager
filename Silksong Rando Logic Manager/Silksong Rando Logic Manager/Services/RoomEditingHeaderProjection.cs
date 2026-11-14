using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Ordinary header/proposal affected-scope read. No complete room loader, scene
/// loader, map geometry or tracked precommit payload is used. A single new read
/// snapshot supplies both scalar values and their matching observation stamps.
/// Restore UTC kind on SQLite audit values at the contract boundary without
/// changing their ticks or the independently converted cache observations.
/// </summary>
internal sealed class RoomEditingHeaderProjection(IDbContextFactory<LogicDbContext> contexts, SceneImageFileService images)
{
    public async Task<ImmutableArray<Wire.RoomDocumentDelta>> LoadAsync(RoomHeaderMutationEffect effect)
    {
        if (effect.AffectedRoomIds.IsEmpty) return [];
        using var imageAdmission = await images.AcquireLifecycleAsync(CancellationToken.None);
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var snapshot = await db.Database.BeginTransactionAsync(CancellationToken.None);
        if (effect.SharedPlan is { } plan) await plan.VerifyAsync(db, CancellationToken.None);
        var ids = effect.AffectedRoomIds.ToArray();
        var headers = await db.Rooms.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new
        {
            Observation = new Wire.RoomDocumentObservation(x.Id, x.CacheFlagUtc),
            Header = new Wire.RoomHeaderSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived),
                new(x.FriendlyName, x.ReferenceId, x.InGameId, x.Contributors, x.Comments), x.RoomGroupId, x.SortOrder,
                new(x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY, x.SceneImagePixelsPerUnitX,
                    x.SceneImagePixelsPerUnitY, x.SceneImagePixelOffsetX, x.SceneImagePixelOffsetY,
                    x.UseSceneImage, x.UseUploadedSceneImage, x.IsSceneImageStale,
                    x.SceneImageOriginalHash, x.SceneImageCropHash, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc)))
                { CompletionStatus = (Wire.RoomStatus)x.CompletionStatus }
        }).ToListAsync();
        // A later external deletion is not a failed write or an empty editable
        // room. Let exceptional read recovery provide the complete tombstone path.
        if (headers.Count != ids.Length)
            throw new InvalidOperationException("An affected room was deleted before its authoritative projection was read.");

        var transitions = effect.ReferencesResolved
            ? await db.RoomTransitions.AsNoTracking().Where(x => ids.Contains(x.RoomId))
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Select(x => new
                {
                    x.RoomId,
                    Row = new Wire.RoomTransitionSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                        new(x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText,
                            x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified, x.IsUnsafeArrival),
                        new(x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ,
                            x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY),
                        x.EnableAnnotation, x.RequirementsParseSucceeded, x.ResolvedSourceSubroomId,
                        x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId)
                }).ToListAsync() : null;
        var deltas = ImmutableArray.CreateBuilder<Wire.RoomDocumentDelta>();
        foreach (var room in headers)
        {
            var advance = effect.Advances.SingleOrDefault(x => x.RoomId == room.Observation.RoomId);
            // Coverage is certifiable only when no later writer changed the room
            // between our known commit and this payload snapshot. Otherwise this
            // delta remains a valid partial observation, not complete freshness.
            Wire.RoomDocumentObservation? covers = advance is not null && advance.After == room.Observation.CacheFlagUtc
                ? new(advance.RoomId, advance.Before) : null;
            var rows = transitions?.Where(x => x.RoomId == room.Observation.RoomId).Select(x => x.Row).ToImmutableArray() ?? [];
            deltas.Add(new(room.Observation, covers, RoomBundleAssembly.Complete(room.Header, images),
                [], rows, [], [], new([], [], [], []), null,
                transitions is null ? null : new(
                    rows.Where(x => !x.Observation.IsArchived).Select(x => x.Observation.EntityId).ToImmutableArray(),
                    rows.Where(x => x.Observation.IsArchived).Select(x => x.Observation.EntityId).ToImmutableArray()),
                null, null, Wire.RoomSceneConcerns.None, null));
        }
        return deltas.ToImmutable();
    }
}
