using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Typed creation consumers at the existing catalogue transactions. Transition
/// creation is owned by its inverse-proposal workflow, not routed through a blind
/// direct creator here. No catch-all maps a catalogue exception to a no-write result.
/// </summary>
internal sealed class RoomEditingCreationCommands(LogicCatalogService catalogue)
{
    public async Task<RoomCreationAdmission?> CreateSubroomAsync(RoomEditingTracker.Entry entry)
    {
        var item = entry.NextOperation is { Operation: Wire.CreateRoomSubroom } head ? head
            : throw new RoomTrackingProtocolException("The admitted head is not a subroom creation.");
        var command = (Wire.CreateRoomSubroom)item.Operation;
        var admission = new RoomCreationAdmission(entry, item, command.EntityId);
        try
        {
            await catalogue.CreateSubroomWithOutcomeAsync(entry.Identity.RoomId,
                new Subroom { FriendlyName = command.Submitted.FriendlyName, ReferenceId = command.Submitted.ReferenceId,
                    Notes = command.Submitted.Notes }, admission, CancellationToken.None);
            return admission;
        }
        catch (RoomCreationIdentityCollisionException) { Collision(entry, item, command.EntityId); return null; }
        catch (RoomCreationMissingOwnerException) { Missing(entry, item); return null; }
    }

    public async Task<RoomCreationAdmission?> CreateConnectionAsync(RoomEditingTracker.Entry entry)
    {
        var item = entry.NextOperation is { Operation: Wire.CreateRoomConnection } head ? head
            : throw new RoomTrackingProtocolException("The admitted head is not a connection creation.");
        var command = (Wire.CreateRoomConnection)item.Operation;
        var value = command.Submitted;
        var admission = new RoomCreationAdmission(entry, item, command.EntityId);
        try
        {
            await catalogue.CreateConnectionWithOutcomeAsync(entry.Identity.RoomId,
                new SubroomConnection { Alias = value.Alias, FriendlyName = value.FriendlyName,
                    SourceSubroomReferenceText = value.SourceSubroomReferenceText,
                    DestinationSubroomReferenceText = value.DestinationSubroomReferenceText,
                    Requirements = value.Requirements, Notes = value.Notes, IsTodo = value.IsTodo,
                    IsVerified = value.IsVerified }, admission, CancellationToken.None);
            return admission;
        }
        catch (RoomCreationIdentityCollisionException) { Collision(entry, item, command.EntityId); return null; }
        catch (RoomCreationMissingOwnerException) { Missing(entry, item); return null; }
    }

    public async Task<RoomCreationAdmission?> CreateCheckAsync(RoomEditingTracker.Entry entry)
    {
        var item = entry.NextOperation is { Operation: Wire.CreateRoomCheck } head ? head
            : throw new RoomTrackingProtocolException("The admitted head is not a check creation.");
        var command = (Wire.CreateRoomCheck)item.Operation;
        var value = command.Submitted;
        var admission = new RoomCreationAdmission(entry, item, command.EntityId);
        try
        {
            await catalogue.CreateCheckWithOutcomeAsync(entry.Identity.RoomId,
                new CheckLocation { FriendlyName = value.FriendlyName, SubroomReferenceText = value.SubroomReferenceText,
                    Requirements = value.Requirements, Notes = value.Notes, LocationType = value.LocationType,
                    IsTodo = value.IsTodo, IsVerified = value.IsVerified }, admission, CancellationToken.None);
            return admission;
        }
        catch (RoomCreationIdentityCollisionException) { Collision(entry, item, command.EntityId); return null; }
        catch (RoomCreationMissingOwnerException) { Missing(entry, item); return null; }
    }

    private static void Collision(RoomEditingTracker.Entry entry, Wire.RoomQueuedOperation item, Guid entityId) =>
        entry.RecordBlocked(new(item.Sequence, item.OperationId, Wire.RoomBlockedDisposition.CorrectiveBlocked,
            Guid.NewGuid(), 1, "The permanent creation identity already exists. Nothing was created. Copy pending work before reloading.",
            new Wire.RoomReloadRequiredEvidence(entityId)));
    private static void Missing(RoomEditingTracker.Entry entry, Wire.RoomQueuedOperation item) =>
        entry.RecordBlocked(new(item.Sequence, item.OperationId, Wire.RoomBlockedDisposition.MissingBlocked,
            Guid.NewGuid(), 1, "The owning room no longer exists. Nothing was created. Copy pending work before reloading.",
            new Wire.RoomReloadRequiredEvidence(entry.Identity.RoomId)));
}
