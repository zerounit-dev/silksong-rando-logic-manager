using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    // Navigation ownership outlives the read lease, but never controls the
    // target's enabled-render gate (CanUseRoom).
    public sealed class RoomSwitch(Guid destination)
    {
        public Guid Destination { get; } = destination;
        public RoomTrackingOwner? Target { get; internal set; }
        public bool Pending { get; internal set; } = true;
    }
    private RoomSwitch? roomSwitch;
    public bool RoomSwitchPending => roomSwitch is not null;
    public RoomSwitch BeginRoomSwitch(Guid destination)
    {
        if (roomSwitch is not null) throw new InvalidOperationException("A room switch is already owned.");
        var value = roomSwitch = new(destination);
        StateHasChanged();
        return value;
    }
    public void BindRoomSwitch(RoomSwitch value, RoomEditing.RoomVisitEntry target)
    {
        if (!ReferenceEquals(roomSwitch, value) || value.Destination != target.RoomId)
            throw new InvalidOperationException("The room switch has retired.");
        value.Target = target.Owner;
    }
    public void EndRoomSwitch(RoomSwitch? value, RoomTrackingOwner? target = null)
    {
        if (value is null || !ReferenceEquals(roomSwitch, value) || target is not null && value.Target != target) return;
        value.Pending = false;
        roomSwitch = null;
        if (!disposed) { StateHasChanged(); _ = DrainRoomRefreshAsync(); }
    }
}
