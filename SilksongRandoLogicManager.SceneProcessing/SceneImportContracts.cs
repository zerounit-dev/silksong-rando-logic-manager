namespace Silksong_Rando_Logic_Manager.Services;

public sealed record SceneImportRoom(Guid Id, string FriendlyName, string ReferenceId, string? InGameId, double? SceneMaxX, double? SceneMaxY)
{
    public double SceneMinX { get; init; }
    public double SceneMinY { get; init; }
    public double? ScaleX { get; init; }
    public double? ScaleY { get; init; }
    public double? OffsetX { get; init; }
    public double? OffsetY { get; init; }
    public bool UseSceneImage { get; init; }
    public bool IsStale { get; init; }
    public long ImageVersion { get; init; }
    public string? ImageUrl { get; init; }
    public bool ImageEligible => UseSceneImage && !IsStale && SceneBounds.TryGetSize(SceneMinX, SceneMinY, SceneMaxX, SceneMaxY, out _, out _) &&
        SceneBounds.HasUsableScales(ScaleX, ScaleY) && SceneBounds.HasUsablePixelOffsets(OffsetX, OffsetY);
}
public sealed record SceneImportPreview(IReadOnlyList<SceneImportRoom> MatchingRooms, IReadOnlyList<SceneImportObjectMatch> ObjectMatches, IReadOnlyList<SceneImportSelectableRecord> Transitions, IReadOnlyList<SceneImportSelectableRecord> Checks, IReadOnlyList<SceneImportEnemyName> EnemyNames);
public sealed record SceneImportEnemyName(Guid Id, string OutputValue, string Name);
public sealed record SceneImportObjectMatch(Guid ObjectId, SceneDumpClassification Classification, string? GameId, IReadOnlyList<SceneImportRecordMatch> Matches);
public sealed record SceneImportRecordMatch(Guid RecordId, Guid RoomId, SceneDumpClassification Classification);
public sealed record SceneImportSelectableRecord(Guid Id, Guid RoomId, SceneDumpClassification Classification, string FriendlyName, string? InGameId)
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public string? LocationType { get; init; }
    public string Requirements { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public SceneEnemyTargetEquivalence? EnemyEquivalence { get; init; }
    public bool IsEnemy => CheckLocationTypeCatalogue.IsEnemy(LocationType);
}
// Modal matching facts only; never a persisted identity or bulk artifact contract.
public sealed record SceneEnemyTargetEquivalence(bool? RequirementsParseSucceeded, bool IsTodo, bool? IsVerified,
    bool EnableAnnotation, double? AnnotationX, double? AnnotationY, string? SubroomReferenceText,
    Guid? ResolvedSubroomId, double? LocalX, double? LocalY, double? LocalZ);
public sealed record SceneWorkbenchRow(SceneImportInstruction Instruction, bool Apply, bool Unresolved, string Requirements, string Notes);
public sealed record SceneWorkbenchArchive(Guid RecordId, SceneDumpClassification Classification);
public sealed record SceneWorkbenchCommand(Guid? TargetRoomId, IReadOnlyList<SceneWorkbenchRow> Rows,
    IReadOnlyList<SceneWorkbenchArchive> Archives, bool ApplyBounds, SceneUnitSize? ReplacementDimensions);
public sealed class SceneWorkbenchValidationException(string message) : InvalidOperationException(message) { }
public sealed record SceneImportInstruction(Guid ObjectId, SceneImportMode Mode, Guid? ExistingRecordId, string FriendlyName);
public enum SceneImportMode { Create, Update }
public sealed record SceneImportResult(Guid RoomId, IReadOnlyList<Guid> TransitionIds, IReadOnlyList<Guid> CheckIds)
{
    public string? HistoryWarning { get; init; }
    public IReadOnlyList<Guid> AppliedExitIds { get; init; } = [];
}
