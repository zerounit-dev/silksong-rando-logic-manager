using System.Text.Json.Serialization;

namespace SilksongRandoLogicManager.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<RoomStatus>))]
public enum RoomStatus
{
    [JsonStringEnumMemberName("neutral")] Neutral,
    [JsonStringEnumMemberName("success")] Success,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("danger")] Danger
}
[JsonConverter(typeof(JsonStringEnumConverter<ImageState>))]
public enum ImageState
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("needsRecapture")] NeedsRecapture,
    [JsonStringEnumMemberName("missing")] Missing,
    [JsonStringEnumMemberName("nonstandardScale")] NonstandardScale
}
public sealed record Sidebar(IReadOnlyList<Group> Groups, IReadOnlyList<Room> Rooms);
public sealed record Group(Guid Id, string FriendlyName, int SortOrder);
public sealed record Room(Guid Id, Guid? GroupId, string FriendlyName, string? InGameId,
    int SortOrder, bool IsArchived, RoomStatus Status, ImageState ImageState);
public sealed record GroupBaseline([property: JsonRequired] Guid Id, [property: JsonRequired] string FriendlyName,
    [property: JsonRequired] string? ZoneReferenceText, [property: JsonRequired] bool IsVirtual, [property: JsonRequired] DateTime UpdatedUtc);
public sealed record GroupValues([property: JsonRequired] string FriendlyName, [property: JsonRequired] string? ZoneReferenceText, [property: JsonRequired] bool IsVirtual);
public sealed record GroupEditor(GroupBaseline Group, IReadOnlyList<string> ZoneIds, string? ZoneReferenceStatus);
public sealed record Location(Guid RoomId, string FriendlyName);
public sealed record Requirement(Guid RoomId, string Requirements);
public sealed record RequirementsData(IReadOnlyList<Requirement> Transitions, IReadOnlyList<Requirement> Connections, IReadOnlyList<Requirement> Checks);
public sealed record SearchData(IReadOnlyList<Location> Locations, RequirementsData Requirements);
public sealed record MutationResult(SharedMutationSegment Shared, GroupEditor? GroupEditor = null, Guid? CreatedRoomId = null, int? MatchedRoomCount = null);
public sealed record ApiError(string Code, string Message, string? TraceId = null, SharedMutationSegment? Shared = null, GroupEditor? GroupEditor = null);
public sealed record SaveGroup([property: JsonRequired] GroupBaseline Baseline, [property: JsonRequired] GroupValues Values);
public sealed record MoveGroup([property: JsonRequired] int Index, [property: JsonRequired] IReadOnlyList<Guid> ExpectedGroupIds);
public sealed record CreateRoom(Guid? GroupId);
public sealed record MoveRoom(Guid? TargetGroupId, [property: JsonRequired] int Index, Guid? ExpectedSourceGroupId,
    [property: JsonRequired] IReadOnlyList<Guid> ExpectedSourceRoomIds, [property: JsonRequired] IReadOnlyList<Guid> ExpectedTargetRoomIds);
