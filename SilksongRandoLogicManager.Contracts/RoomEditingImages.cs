using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

public sealed record RoomImageOpenRequest(RoomTrackingOwner Owner, Guid OpenAttemptId, long DialogGeneration);
public sealed record RoomImageDraftView(Guid DraftOwnerId, long DialogGeneration, long Revision,
    RoomSceneImageBaseline Baseline, RoomSceneImageSettings Settings, double SourceWidth, double SourceHeight,
    bool MapSourceReady, string? PreviewUrl, string? Information);
public enum RoomImageUploadStatus { Receiving, Ready, Failed, Missing }
public sealed record RoomImageUploadView(Guid UploadAttemptId, RoomImageUploadStatus Status,
    Guid? Handle, uint? Width, uint? Height, string? Hash);
public sealed record RoomImageSelectRequest(RoomTrackingOwner Owner, Guid RequestId, long Revision,
    RoomSceneImageSource Source, Guid? UploadAttemptId);
public sealed record RoomImageEstimateRequest(RoomTrackingOwner Owner, Guid RequestId, long Revision,
    RoomSceneImageSettings Settings, Guid? UploadAttemptId, SceneImageUploadPreparation? Preparation);
public sealed record RoomImageReleaseRequest(RoomTrackingOwner Owner, Guid ReleaseId, ImmutableArray<Guid> UploadAttemptIds);
