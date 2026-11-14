using System.Collections.Immutable;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Exceptional known-receipt recovery. It has no command/catalogue/image publisher
/// dependency and cannot execute a registered operation. Every replacement is a
/// later complete per-room read, not a claim to reproduce the exact commit state
/// or one multiroom world snapshot. Caller holds the same visit gate throughout.
/// </summary>
internal sealed class RoomEditingRecovery(RoomEditingDocumentLoader documents, SharedCatalogueService sharedCatalogue, ILogger<RoomEditingRecovery> logger)
{
    public async Task RecoverProjectionsAsync(RoomEditingTracker.Entry entry)
    {
        if (entry.Invalidated) throw new RoomTrackingUnavailableException();
        foreach (var pending in entry.PendingProjections)
        {
            try
            {
                var complete = ImmutableArray.CreateBuilder<Wire.RoomCompleteDocument>();
                var deleted = ImmutableArray.CreateBuilder<Guid>();
                foreach (var roomId in pending.Receipt.AffectedRoomIds)
                {
                    var document = await documents.LoadAsync(roomId, CancellationToken.None);
                    if (document is null) deleted.Add(roomId);
                    else complete.Add(document);
                }
                // Install only after the entire affected scope succeeded. A failed
                // later room read cannot publish half of a multiroom replacement.
                var replacements = complete.ToImmutable(); var removals = deleted.ToImmutable();
                var shared = await sharedCatalogue.ReadRecoveredMutationAsync(pending.Receipt, replacements, removals);
                entry.InstallProjection(new(pending.Receipt, [], replacements, removals, false) { Shared = shared });
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "Authoritative read recovery failed for saved room operation {OperationId} in tracker {TrackerId}",
                    pending.Receipt.OperationId, entry.Identity.TrackerId);
                // Receipt/T remain durable-in-memory knowledge. Leave the unusable
                // projection pending and pause suffix execution; no mutation retry.
                return;
            }
        }
    }
}
