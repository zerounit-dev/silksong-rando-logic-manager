using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Executable header consumer joining transactional disposition and fallible projection.</summary>
internal sealed class RoomEditingHeaderWorkflow(RoomEditingHeaderCommands commands, RoomEditingHeaderProjection projection,
    SharedCatalogueService sharedCatalogue, ILogger<RoomEditingHeaderWorkflow> logger)
{
    public async Task ExecuteAsync(RoomEditingTracker.Entry entry)
    {
        var sequence = entry.NextOperation?.Sequence
            ?? throw new RoomTrackingProtocolException("The header queue head is not executable.");
        await ExecuteAndProjectAsync(entry, sequence, () => commands.ExecuteAsync(entry));
    }

    public Task ResolveAsync(RoomEditingTracker.Entry entry, Wire.RoomResolveRequest request) =>
        entry.ResolveHeadAsync(request, () => ExecuteAndProjectAsync(entry, request.Sequence,
            () => commands.ResolveAsync(entry, request.Resolution)));

    private async Task ExecuteAndProjectAsync(RoomEditingTracker.Entry entry, long sequence,
        Func<Task<RoomHeaderMutationEffect?>> execute)
    {
        RoomHeaderMutationEffect? effect;
        try { effect = await execute(); }
        catch (Exception failure) when (!entry.Invalidated && entry.Progress.TerminalThrough >= sequence)
        {
            // For example, DB disposal can fail after the reliable receipt seam.
            // The write remains known saved, with read-only recovery available.
            logger.LogError(failure, "Room header operation {Sequence} committed but completion failed in tracker {TrackerId}",
                sequence, entry.Identity.TrackerId);
            return;
        }
        if (entry.Invalidated) throw new RoomTrackingUnavailableException();
        if (effect is null) return; // Typed current blocked head; no receipt/no write.
        var receipt = entry.TerminalReceipt(sequence);
        try
        {
            var changes = await projection.LoadAsync(effect);
            var shared = effect.SharedPlan is { } plan ? await sharedCatalogue.ReadCommittedMutationAsync(plan) : null;
            entry.InstallProjection(new(receipt, changes, [], [], false) { Shared = shared });
        }
        catch (Exception failure)
        {
            logger.LogError(failure, "Room header operation {OperationId} is saved but authoritative projection failed in tracker {TrackerId}",
                receipt.OperationId, entry.Identity.TrackerId);
            // RecordReceipt already advanced T and installed ProjectionPending.
            // Do not throw a false write-rejection or execute the mutation again.
        }
    }
}
