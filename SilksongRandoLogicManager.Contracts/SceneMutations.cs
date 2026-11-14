namespace Silksong_Rando_Logic_Manager.Services;

// Proposed persisted values only. No scene objects or classification evidence cross Apply.
public sealed record SceneMetadata(string? InGameId, double? X, double? Y, double? Z,
    double? LocalX, double? LocalY, double? LocalZ);
public sealed record SceneInitialAnnotation(double? X, double? Y, bool Enabled);
public sealed record SceneDestinationText(string? RoomReference, bool NewTargetRoom, string? TransitionAlias);
public sealed record SceneTransitionCreate(string FriendlyName, SceneMetadata Metadata,
    SceneInitialAnnotation Annotation, string Requirements, SceneDestinationText Destination);
public sealed record SceneTransitionUpdate(Guid Id, SceneMetadata Metadata);
public sealed record SceneCheckCreate(string FriendlyName, SceneMetadata Metadata,
    SceneInitialAnnotation Annotation, string Requirements, string Notes, string? LocationType);
public sealed record SceneCheckUpdate(Guid Id, SceneMetadata Metadata);
public sealed record SceneEnemyUpdate(Guid Id, SceneMetadata Metadata, string Requirements, string Notes);
public enum SceneMutationFamily { Transition, Check, Enemy }
public sealed record SceneArchiveMutation(Guid Id, SceneMutationFamily Family);
public enum SceneBoundsMutationKind { Initialize, Replace }
public sealed record SceneBoundsObservation(double MinX, double MinY, double? MaxX, double? MaxY);
public sealed record SceneBoundsMutation(SceneBoundsMutationKind Kind, SceneBoundsObservation Original, double MaxX, double MaxY);
public sealed record SceneRoomIntent(Guid? ExistingId, string? NewFriendlyName, string? NewInGameId);
public sealed record SceneDestinationRoom(Guid Id, string? InGameId, string ReferenceId);
public sealed record SceneDestinationTransition(Guid Id, Guid RoomId, string? InGameId, string Alias);
public sealed record SceneDestinationObservation(IReadOnlyList<string> Keys, IReadOnlyList<SceneDestinationRoom> Rooms,
    IReadOnlyList<SceneDestinationTransition> Transitions, string Fingerprint);
public sealed record SceneApplyRequest(string SceneName, string Fingerprint, SceneRoomIntent Target,
    IReadOnlyList<string> DestinationKeys, string DestinationFingerprint,
    IReadOnlyList<SceneTransitionCreate> TransitionCreates, IReadOnlyList<SceneTransitionUpdate> TransitionUpdates,
    IReadOnlyList<SceneCheckCreate> CheckCreates, IReadOnlyList<SceneCheckUpdate> CheckUpdates,
    IReadOnlyList<SceneEnemyUpdate> EnemyUpdates, IReadOnlyList<SceneArchiveMutation> Archives, SceneBoundsMutation? Bounds);
