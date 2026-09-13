namespace Silksong_Rando_Logic_Manager.Services;

// These records are the import/reconciliation boundary. They deliberately carry
// exchange values and presentation evidence only; they never carry EF state.
public sealed record DistributedImportPackage(
    int ExportVersion,
    bool? IsPartialRoomDump,
    DistributedAreaMapSnapshot? AreaMap,
    DistributedRoomGroupingSnapshot? RoomGroupings,
    IReadOnlyList<DistributedRoomDocument> Rooms);

public sealed record DistributedImportPackageSummary(
    int ExportVersion,
    bool? IsPartialRoomDump,
    int MapCount,
    int GroupCount,
    int RoomCount,
    int SubroomCount,
    int TransitionCount,
    int ConnectionCount,
    int CheckCount);

public abstract record DistributedImportParseOutcome;
public sealed record DistributedImportPackageValidated(DistributedImportPackage Package, DistributedImportPackageSummary Summary) : DistributedImportParseOutcome;
public sealed record DistributedImportPackageRejected(IReadOnlyList<string> Errors) : DistributedImportParseOutcome;

public enum DistributedComparisonEvidence { Neutral, Current, Incoming, Changed }
public enum DistributedRoomComparisonKind { Identical, New, Changed }
public enum DistributedUpdatedUtcComparison { Older, Same, Newer }
public sealed record DistributedComparisonValue(string FieldName, string? CurrentValue, string? IncomingValue, DistributedComparisonEvidence Evidence);
public sealed record DistributedRoomComparisonSummary(DistributedUpdatedUtcComparison UpdatedUtc, int HeaderFieldChanges, int SubroomChanges, int TransitionChanges, int ConnectionChanges, int CheckChanges);
// Child comparison evidence is deliberately field-shaped.  The UI can retain
// table-column alignment without receiving whole current/incoming row objects.
public sealed record DistributedChildComparison<T>(Guid EntityId, DistributedComparisonEvidence Evidence, IReadOnlyList<DistributedComparisonValue> Fields);
public static class DistributedChildComparisonColumns
{
    public static readonly IReadOnlyList<string> Subrooms = ["id", "referenceId", "friendlyName", "notes", "sceneUnitX", "sceneUnitY", "sceneUnitWidth", "sceneUnitHeight", "enableAnnotation", "sortOrder", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"];
    public static readonly IReadOnlyList<string> Transitions = ["id", "alias", "friendlyName", "inGameId", "inGamePositionX", "inGamePositionY", "inGamePositionZ", "localPositionX", "localPositionY", "localPositionZ", "annotationSceneUnitX", "annotationSceneUnitY", "enableAnnotation", "sourceSubroomReferenceText", "destinationRoomReferenceText", "destinationTransitionAliasText", "requirements", "notes", "sortOrder", "isTodo", "isVerified", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"];
    public static readonly IReadOnlyList<string> Connections = ["id", "alias", "friendlyName", "sourceSubroomReferenceText", "destinationSubroomReferenceText", "requirements", "notes", "enableAnnotation", "sceneUnitX", "sceneUnitY", "sortOrder", "isTodo", "isVerified", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"];
    public static readonly IReadOnlyList<string> Checks = ["id", "friendlyName", "inGameId", "inGamePositionX", "inGamePositionY", "inGamePositionZ", "localPositionX", "localPositionY", "localPositionZ", "annotationSceneUnitX", "annotationSceneUnitY", "subroomReferenceText", "requirements", "notes", "locationType", "enableAnnotation", "sortOrder", "isTodo", "isVerified", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"];
}
public sealed record DistributedRoomComparison(
    Guid RoomId,
    DistributedRoomComparisonKind Kind,
    DistributedRoomComparisonSummary? Summary,
    IReadOnlyList<DistributedComparisonValue> TopLevelFields,
    IReadOnlyList<DistributedChildComparison<DistributedSubroom>> Subrooms,
    IReadOnlyList<DistributedChildComparison<DistributedTransition>> Transitions,
    IReadOnlyList<DistributedChildComparison<DistributedConnection>> Connections,
    IReadOnlyList<DistributedChildComparison<DistributedCheck>> Checks,
    IReadOnlyList<DistributedImportWarning> Warnings);

public sealed record DistributedImportWarning(string Code, string Message);
public sealed record DistributedImportRoomIdentity(Guid Id, string FriendlyName, string? InGameId);
public abstract record DistributedRoomDecisionIntent(Guid RoomId);
public sealed record SkipIncomingRoom(Guid RoomId) : DistributedRoomDecisionIntent(RoomId);
public sealed record ImportIncomingRoom(Guid RoomId) : DistributedRoomDecisionIntent(RoomId);
public sealed record KeepMasterRoom(Guid RoomId) : DistributedRoomDecisionIntent(RoomId);
public sealed record UseIncomingRoom(Guid RoomId) : DistributedRoomDecisionIntent(RoomId);
public sealed record DistributedImportProgress(string Stage, int Completed, int Total, string? Detail);
public abstract record DistributedImportOutcome;
public sealed record DistributedImportPrepared(DistributedImportPackageSummary Summary) : DistributedImportOutcome;
public sealed record DistributedImportRoomPrepared(DistributedRoomComparison Comparison) : DistributedImportOutcome;
public sealed record DistributedImportStartRequest(DistributedImportPackage Package, bool ApplyAreaMap, bool ApplyRoomGroupings);
// This is caller-owned, transient orchestration state. It is deliberately not an
// import session and contains no EF state.
public sealed record DistributedImportOrchestrationState(
    DistributedImportPackage Package,
    bool HasStarted,
    bool AreaMapStageCompleted,
    bool RoomGroupingStageCompleted,
    DistributedImportProgress Progress,
    DistributedImportStageOutcome AreaMapOutcome,
    DistributedImportStageOutcome RoomGroupingOutcome,
    IReadOnlySet<Guid> PendingRoomIds,
    IReadOnlySet<Guid> DecidedRoomIds,
    IReadOnlyList<Guid>? ReviewRoomIds = null)
{
    public DistributedImportOrchestrationState(DistributedImportPackage package, bool hasStarted, bool areaMapStageCompleted, bool roomGroupingStageCompleted)
        : this(package, hasStarted, areaMapStageCompleted, roomGroupingStageCompleted,
            new("unstarted", 0, 0, null), new("area-map", false, false, null),
            new("room-groupings", false, false, null), new HashSet<Guid>(), new HashSet<Guid>(), []) { }
}
public sealed record DistributedImportStageOutcome(string Stage, bool Applied, bool Skipped, string? Detail);
public sealed record DistributedImportStarted(DistributedImportOrchestrationState State, DistributedImportStageOutcome AreaMap, DistributedImportStageOutcome RoomGroupings) : DistributedImportOutcome;
public sealed record DistributedImportRoomSkipped(Guid RoomId, DistributedImportOrchestrationState State) : DistributedImportOutcome;
public sealed record DistributedImportRoomKept(Guid RoomId, DistributedImportOrchestrationState State) : DistributedImportOutcome;
public sealed record DistributedImportRoomApplied(Guid RoomId, DistributedImportOrchestrationState State) : DistributedImportOutcome;
public sealed record DistributedImportComparisonRefreshed(DistributedRoomComparison Comparison) : DistributedImportOutcome;
// The review is caller-owned transient state.  Its IDs are the sole authority
// for a later archive decision; package membership is not a substitute for the
// list the user actually reviewed.
public sealed record DistributedImportSweepReview(IReadOnlyList<Guid> RoomIds);
public sealed record DistributedImportSweepPrepared(IReadOnlyList<DistributedImportSweepRoom> Rooms, DistributedImportSweepReview Review) : DistributedImportOutcome;
public sealed record DistributedImportSweepCompleted(bool Archived) : DistributedImportOutcome;
public sealed record DistributedImportFailed(string Stage, string Message, DistributedImportOrchestrationState? State = null) : DistributedImportOutcome;
public sealed record DistributedImportRejected(IReadOnlyList<string> Errors) : DistributedImportOutcome;
public sealed record DistributedImportSweepRoom(Guid RoomId, string FriendlyName);

// Persistence loaders use these scalar records; the mapper keeps entity materialization
// on the persistence/application-service side of the exchange boundary.
public sealed record DistributedImportRoomProjection(
    Guid Id, Guid? RoomGroupId, string ReferenceId, string FriendlyName, string? InGameId,
    string? Contributors, string? Comments, double? SceneUnitWidth, double? SceneUnitHeight,
    double? SceneImageScaleXPercent, double? SceneImageScaleYPercent, double? SceneImagePanXPercent,
    double? SceneImagePanYPercent, bool IsSceneImageStale, int SortOrder, bool IsArchived,
    DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc,
    IReadOnlyList<DistributedImportSubroomProjection> Subrooms,
    IReadOnlyList<DistributedImportTransitionProjection> Transitions,
    IReadOnlyList<DistributedImportConnectionProjection> Connections,
    IReadOnlyList<DistributedImportCheckProjection> Checks);
public sealed record DistributedImportSubroomProjection(Guid Id, string ReferenceId, string FriendlyName, string? Notes, double? SceneUnitX, double? SceneUnitY, double? SceneUnitWidth, double? SceneUnitHeight, bool EnableAnnotation, int SortOrder, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedImportTransitionProjection(Guid Id, string Alias, string FriendlyName, string? InGameId, double? InGamePositionX, double? InGamePositionY, double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ, double? AnnotationSceneUnitX, double? AnnotationSceneUnitY, bool EnableAnnotation, string? SourceSubroomReferenceText, string? DestinationRoomReferenceText, string? DestinationTransitionAliasText, string Requirements, string Notes, int SortOrder, bool IsTodo, bool? IsVerified, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedImportConnectionProjection(Guid Id, string Alias, string FriendlyName, string SourceSubroomReferenceText, string DestinationSubroomReferenceText, string Requirements, string Notes, bool EnableAnnotation, double? SceneUnitX, double? SceneUnitY, int SortOrder, bool IsTodo, bool? IsVerified, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedImportCheckProjection(Guid Id, string FriendlyName, string? InGameId, double? InGamePositionX, double? InGamePositionY, double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ, double? AnnotationSceneUnitX, double? AnnotationSceneUnitY, string? SubroomReferenceText, string Requirements, string Notes, string? LocationType, bool EnableAnnotation, int SortOrder, bool IsTodo, bool? IsVerified, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);

public static class DistributedImportProjectionMapper
{
    public static DistributedRoomDocument MapRoom(DistributedImportRoomProjection source) => new(
        source.Id, source.RoomGroupId, source.ReferenceId, source.FriendlyName, source.InGameId, source.Contributors, source.Comments,
        source.SceneUnitWidth, source.SceneUnitHeight, source.SceneImageScaleXPercent, source.SceneImageScaleYPercent, source.SceneImagePanXPercent, source.SceneImagePanYPercent,
        source.IsSceneImageStale, source.SortOrder, source.IsArchived, source.ArchivedUtc, source.CreatedUtc, source.UpdatedUtc,
        source.Subrooms.Select(x => new DistributedSubroom(x.Id, x.ReferenceId, x.FriendlyName, x.Notes, x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation, x.SortOrder, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc)).ToArray(),
        source.Transitions.Select(x => new DistributedTransition(x.Id, x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.EnableAnnotation, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.SortOrder, x.IsTodo, x.IsVerified, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc)).ToArray(),
        source.Connections.Select(x => new DistributedConnection(x.Id, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY, x.SortOrder, x.IsTodo, x.IsVerified, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc)).ToArray(),
        source.Checks.Select(x => new DistributedCheck(x.Id, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SubroomReferenceText, x.Requirements, x.Notes, x.LocationType, x.EnableAnnotation, x.SortOrder, x.IsTodo, x.IsVerified, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc)).ToArray());
}
