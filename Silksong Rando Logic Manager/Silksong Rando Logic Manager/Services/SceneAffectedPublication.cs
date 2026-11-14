using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

// Uses the sole shared admission and common frozen-effect footprint; projection
// stays fallible after the existing reliable receipt.
internal static class SceneAffectedPublication
{
    internal sealed record Plan(SharedPublicationCoordinator.PreparedPublication Publication,
        SharedRoomCommitPlan Shared,
        SceneAffectedRecovery Recovery);

    internal static async Task<Plan> PrepareAsync(LogicDbContext db, SharedPublicationCoordinator.Admission admission,
        LogicWriteScope transaction, IReadOnlyCollection<Guid> suppliedRooms)
    {
        var effects = transaction.FinalEffects;
        var prepared = admission.PrepareAdvance(effects.RoomFactsChanged, false);
        var shared = await SharedRoomCommitPlan.PrepareAsync(db, transaction, prepared, CancellationToken.None, suppliedRooms);
        return new(prepared, shared, new(shared.AffectedRoomIds, effects.Groups.Keys.ToArray()));
    }

    internal static async Task<SharedMutationSegment> ProjectAsync(LogicDbContext db, Plan plan, SceneImageFileService? images)
    {
        if (images is null) throw new InvalidOperationException("Room bundle metadata requires the image lifecycle owner.");
        await plan.Shared.VerifyAsync(db, CancellationToken.None);
        return await SharedCatalogueService.AssembleMutationAsync(db, plan.Shared, images, CancellationToken.None);
    }
}
