using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class LogicCatalogService
{
    internal async Task<RoomMutationEffect?> WriteTrackedExplanationAsync(RoomEditingTracker.Entry entry)
    {
        var (publication, actor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var admission = publication;
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, actor, CancellationToken.None);
        var roomId = entry.Identity.RoomId;
        var scope = await RoomEditingMutationScope.CaptureAsync(db, entry, publication);
        RequirementExplanation row;
        Wire.RoomExplanationValues value;
        Guid? createdId = null;
        switch (entry.CurrentHead?.Operation)
        {
            case Wire.CreateRoomExplanation create:
                if (!await db.Rooms.AnyAsync(x => x.Id == roomId))
                { RoomEditingMutationScope.Missing(entry, roomId); return null; }
                if (await db.RequirementExplanations.AnyAsync(x => x.Id == create.EntityId))
                {
                    RoomEditingMutationScope.Corrective(entry, create.EntityId,
                        "The permanent creation identity already exists. Nothing was created. Copy pending work before reloading.");
                    return null;
                }
                var last = await db.RequirementExplanations.Where(x => x.RoomId == roomId && !x.IsArchived)
                    .MaxAsync(x => (int?)x.SortOrder) ?? -1;
                row = new() { Id = create.EntityId, RoomId = roomId, SortOrder = checked(last + 1) };
                db.RequirementExplanations.Add(row);
                createdId = row.Id;
                value = create.Submitted;
                break;
            case Wire.SaveRoomExplanation save:
                var current = await db.RequirementExplanations.SingleOrDefaultAsync(x => x.Id == save.EntityId
                    && x.RoomId == roomId && !x.IsArchived);
                if (current is null) { RoomEditingMutationScope.Missing(entry, save.EntityId); return null; }
                row = current;
                var original = new Wire.RoomExplanationValues(row.FriendlyName, row.ReferenceId, row.Link, row.Notes);
                var merge = Wire.RoomEditingMerge.Explanation(save.Origin.Original, save.Submitted, original, entry.ExplanationReview);
                if (!merge.CanApply)
                {
                    entry.ExplanationReview = null;
                    RoomEditingMutationScope.Block(entry, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                        merge.ReviewSuperseded ? "The reviewed explanation changed again. Review the current values." : "This explanation changed elsewhere.",
                        new Wire.RoomExplanationConflictEvidence(original, save.Submitted,
                            Wire.RoomEditingMerge.Explanation(save.Origin.Original, save.Submitted, original).DivergentFields));
                    return null;
                }
                value = merge.Values;
                break;
            default:
                throw new RoomTrackingProtocolException("The head is not an explanation write.");
        }
        row.FriendlyName = value.FriendlyName;
        row.ReferenceId = value.ReferenceId;
        row.Link = value.Link;
        row.Notes = value.Notes;
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, transaction, RoomProjectionFamilies.Explanations, Wire.RoomSceneConcerns.None,
            createdId: createdId);
    }
}
