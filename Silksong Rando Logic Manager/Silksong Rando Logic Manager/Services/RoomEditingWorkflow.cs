using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Closed operation dispatch under the visit gate; never a batch transaction.</summary>
internal sealed class RoomEditingWorkflow(LogicCatalogService catalogue, RoomEditingHeaderWorkflow headers,
    RoomEditingSubroomCommands subrooms, RoomEditingCreationCommands creations, RoomEditingImages images,
    RoomEditingMutationProjection projection, SharedCatalogueService sharedCatalogue, ILogger<RoomEditingWorkflow> logger, Authentication.LiveAccess access)
{
    public async Task ExecutePrefixAsync(RoomEditingTracker.Entry entry)
    {
        while (entry.NextOperation is { } head)
        {
            await access.RequireOwnerAsync(entry.SessionOwner, Authentication.AccessPolicies.Author);
            if (head.Operation is Wire.SaveRoomHeader) await headers.ExecuteAsync(entry);
            else await ExecuteAndProjectAsync(entry, head.Sequence, () => ExecuteHeadAsync(entry));
            if (entry.Invalidated) throw new RoomTrackingUnavailableException();
            if (entry.Progress.TerminalThrough < head.Sequence || entry.ProjectionPending) break;
        }
        await images.CollectAsync(entry);
    }

    public Task ResolveAsync(RoomEditingTracker.Entry entry, Wire.RoomResolveRequest request)
    {
        if (entry.HandleAcknowledgedResolution(request)) return Task.CompletedTask;
        if (entry.CurrentHead?.Operation is Wire.SaveRoomHeader) return headers.ResolveAsync(entry, request);
        return entry.ResolveHeadAsync(request, async () =>
        {
            await ExecuteAndProjectAsync(entry, request.Sequence, () => ResolveHeadAsync(entry, request.Resolution));
            await images.CollectAsync(entry);
            // Resolution deliberately never drains the registered suffix. The
            // client must incorporate it first and explicitly resume with new I.
        });
    }

    private async Task<RoomMutationEffect?> ExecuteHeadAsync(RoomEditingTracker.Entry entry)
    {
        switch (entry.CurrentHead?.Operation)
        {
            case Wire.CreateRoomSubroom: return Created(entry, await creations.CreateSubroomAsync(entry),
                RoomProjectionFamilies.Subrooms | RoomProjectionFamilies.Transitions | RoomProjectionFamilies.Connections | RoomProjectionFamilies.Checks);
            case Wire.CreateRoomConnection: return Created(entry, await creations.CreateConnectionAsync(entry), RoomProjectionFamilies.Connections);
            case Wire.CreateRoomCheck: return Created(entry, await creations.CreateCheckAsync(entry), RoomProjectionFamilies.Checks);
            case Wire.SaveRoomSubroom: return await subrooms.ExecuteAsync(entry);
            case Wire.CreateRoomExplanation or Wire.SaveRoomExplanation: return await catalogue.WriteTrackedExplanationAsync(entry);
            case Wire.CreateRoomTransition or Wire.SaveRoomTransition or Wire.RequestRoomTransitionInverse:
                return await catalogue.ExecuteTrackedTransitionAsync(entry);
            case Wire.SaveRoomConnection: return await catalogue.SaveTrackedConnectionAsync(entry);
            case Wire.SaveRoomCheck: return await catalogue.SaveTrackedCheckAsync(entry);
            case Wire.SaveRoomTransitionMetadata or Wire.SaveRoomCheckMetadata: return await catalogue.SaveTrackedMetadataAsync(entry);
            case Wire.SaveRoomSubroomGeometry: return await catalogue.SaveTrackedSubroomGeometryAsync(entry);
            case Wire.ChangeRoomSubroomAnnotation: return await catalogue.ChangeTrackedSubroomAnnotationAsync(entry);
            case Wire.ChangeRoomTransitionAnnotation or Wire.ChangeRoomCheckAnnotation: return await catalogue.ChangeTrackedDirectAnnotationAsync(entry);
            case Wire.ChangeRoomConnectionAnnotation: return await catalogue.ChangeTrackedConnectionAnnotationAsync(entry);
            case Wire.ScaffoldRoomConnectionInverse: return await catalogue.ScaffoldTrackedConnectionAsync(entry);
            case Wire.SetRoomArchive or Wire.SetRoomSubroomArchive or Wire.SetRoomTransitionArchive or Wire.SetRoomConnectionArchive or Wire.SetRoomCheckArchive or Wire.SetRoomExplanationArchive:
                return await catalogue.SetTrackedArchiveAsync(entry);
            case Wire.ReorderRoomSubroom or Wire.ReorderRoomTransition or Wire.ReorderRoomConnection or Wire.ReorderRoomCheck or Wire.ReorderRoomExplanation:
                return await catalogue.ReorderTrackedChildAsync(entry);
            case Wire.DeleteRoomSubroom or Wire.DeleteRoomTransition or Wire.DeleteRoomConnection or Wire.DeleteRoomCheck or Wire.DeleteRoomExplanation:
                return await catalogue.DeleteTrackedChildAsync(entry);
            case Wire.DeleteRoom: return await catalogue.DeleteTrackedRoomAsync(entry);
            case Wire.RecheckRoomRequirements: return await catalogue.RecheckTrackedRoomAsync(entry);
            case Wire.ApplyRoomSceneImage or Wire.ClearRoomSceneBounds: return await images.ExecuteAsync(entry);
            default: throw new RoomTrackingProtocolException("There is no executable typed room head.");
        }
    }

    private Task<RoomMutationEffect?> ResolveHeadAsync(RoomEditingTracker.Entry entry, Wire.RoomHeadResolution resolution)
    {
        if (entry.CurrentHead?.Operation is Wire.SaveRoomSubroom) return subrooms.ResolveAsync(entry, resolution);
        if (entry.CurrentHead?.Operation is Wire.CreateRoomTransition or Wire.SaveRoomTransition or Wire.RequestRoomTransitionInverse)
            return catalogue.ResolveTrackedTransitionAsync(entry, resolution);
        if (resolution is Wire.CorrectRoomSceneImageApply correction) return images.ExecuteAsync(entry, correction);
        if (resolution is not Wire.ChooseRoomConflictValues choice)
            throw new RoomTrackingProtocolException("This resolution has no matching room workflow.");
        switch (entry.Blocked?.Evidence)
        {
            case Wire.RoomExplanationConflictEvidence x: entry.ExplanationReview = new(x.Reviewed, choice.Choice); break;
            case Wire.RoomConnectionConflictEvidence x: entry.ConnectionReview = new(x.Reviewed, choice.Choice); break;
            case Wire.RoomCheckConflictEvidence x: entry.CheckReview = new(x.Reviewed, choice.Choice); break;
            case Wire.RoomMetadataConflictEvidence x: entry.MetadataReview = new(x.Reviewed, choice.Choice); break;
            case Wire.RoomGeometryConflictEvidence x: entry.GeometryReview = new(x.Reviewed, choice.Choice); break;
            case Wire.RoomDirectAnnotationConflictEvidence x: entry.DirectAnnotationReview = new(x.Reviewed, choice.Choice); break;
            case Wire.RoomConnectionAnnotationConflictEvidence x: entry.ConnectionAnnotationReview = new(x.Reviewed, choice.Choice); break;
            default: throw new RoomTrackingProtocolException("The conflict evidence does not match a reviewed typed value.");
        }
        return ExecuteHeadAsync(entry);
    }

    private static RoomMutationEffect? Created(RoomEditingTracker.Entry entry, RoomCreationAdmission? admission, RoomProjectionFamilies family) =>
        admission is null ? null : new(entry.Identity.RoomId, admission.AffectedRoomIds, admission.Advances, family,
            Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport, new([], [], [], [], [])) { SharedPlan = admission.SharedPlan };

    private async Task ExecuteAndProjectAsync(RoomEditingTracker.Entry entry, long sequence, Func<Task<RoomMutationEffect?>> execute)
    {
        RoomMutationEffect? effect;
        try { effect = await execute(); }
        catch (Exception failure) when (!entry.Invalidated && entry.Progress.TerminalThrough >= sequence)
        {
            logger.LogError(failure, "Room operation {Sequence} is saved but completion failed in tracker {TrackerId}", sequence, entry.Identity.TrackerId);
            return; // Reliable receipt retained; read-only recovery, never execution retry.
        }
        if (entry.Invalidated) throw new RoomTrackingUnavailableException();
        if (effect is null) return;
        var receipt = entry.TerminalReceipt(sequence);
        try
        {
            var changes = await projection.LoadAsync(effect);
            var shared = effect.SharedPlan is { } plan ? await sharedCatalogue.ReadCommittedMutationAsync(plan) : null;
            entry.InstallProjection(new(receipt, changes, [], effect.DeletedRoomIds, false) { Shared = shared });
        }
        catch (Exception failure)
        {
            logger.LogError(failure, "Room operation {OperationId} is saved but authoritative projection failed in tracker {TrackerId}", receipt.OperationId, entry.Identity.TrackerId);
        }
    }
}
