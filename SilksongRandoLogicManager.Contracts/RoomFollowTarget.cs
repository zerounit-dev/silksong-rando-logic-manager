namespace SilksongRandoLogicManager.Contracts;

public sealed record RoomFollowTarget(Guid? RoomId);

public static class ArchipelagoRoomNames
{
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static bool Same(string? left, string? right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
