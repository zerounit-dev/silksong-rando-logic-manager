namespace Silksong_Rando_Logic_Manager.Services;

public sealed record RoomGraphExportFailure(
    string Cause,
    string? RoomId = null,
    string? RoomName = null,
    string? ChildKind = null,
    string? ChildName = null,
    Guid? RoomRecordId = null);

public sealed record RoomGraphExportResult(
    byte[]? Content,
    IReadOnlyList<RoomGraphExportFailure> Failures)
{
    public const string FileName = "room_graph_data.json";
    public bool Succeeded => Content is not null && Failures.Count == 0;

    public static RoomGraphExportResult Success(byte[] content) => new(content, []);
    public static RoomGraphExportResult Failed(IReadOnlyList<RoomGraphExportFailure> failures) => new(null, failures);
}

public interface IRoomGraphExportService
{
    Task<RoomGraphExportResult> GenerateAsync(CancellationToken cancellationToken = default);
}

public sealed record RoomGraphDocument(
    int SchemaVersion,
    RoomGraphSource Source,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> AuthoritativeAreas,
    IReadOnlyList<string> MetadataOnlyAreas,
    IReadOnlyList<RoomGraphTranslationPredicate> TranslationPredicates,
    IReadOnlyList<RoomGraphTranslationItem> TranslationItems,
    IReadOnlyDictionary<string, RoomGraphLocationType> LocationTypes,
    IReadOnlyList<RoomGraphRoom> Rooms);

public sealed record RoomGraphSource(string Name, int RoomCount);
public sealed record RoomGraphTranslationPredicate(string Name, string? Category, string InputSyntax, string OutputSyntax, IReadOnlyList<string> Aliases, string Notes);
public sealed record RoomGraphTranslationItem(string Name, string? Category, IReadOnlyList<string> Aliases, string OutputValue, string Notes);
public sealed record RoomGraphLocationType(bool? HasPersistedState, string Description);
public sealed record RoomGraphNode(string Id, string Name, string Notes);
public sealed record RoomGraphRequirement(string Raw, string Mode, IReadOnlyList<IReadOnlyList<string>> Dnf, IReadOnlyList<string> UnresolvedReasons);
public sealed record RoomGraphTransition(string Id, string Alias, string Name, string? InGameId, string SourceNodeId, string? Target, RoomGraphRequirement Requirement, string Notes, bool IsTodo, bool? IsVerified, string LogicStatus, IReadOnlyList<string> UnresolvedReasons);
public sealed record RoomGraphConnection(string Id, string Alias, string Name, string SourceNodeId, string TargetNodeId, RoomGraphRequirement Requirement, string Notes, bool IsTodo, bool? IsVerified, string LogicStatus, IReadOnlyList<string> UnresolvedReasons);
public sealed record RoomGraphLocation(string Id, string Name, string? InGameId, string LocationType, bool? HasPersistedState, string NodeId, RoomGraphRequirement Requirement, string Notes, bool IsTodo, bool? IsVerified, string LogicStatus, IReadOnlyList<string> UnresolvedReasons);
public sealed record RoomGraphRoom(string Id, string SourceRecord, string AreaId, string Name, string? InGameId, string Contributors, string Notes, IReadOnlyList<RoomGraphNode> Nodes, IReadOnlyList<RoomGraphTransition> Transitions, IReadOnlyList<RoomGraphConnection> Connections, IReadOnlyList<RoomGraphLocation> Locations);
