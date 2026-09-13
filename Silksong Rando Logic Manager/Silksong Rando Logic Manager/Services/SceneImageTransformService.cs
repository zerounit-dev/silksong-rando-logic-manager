using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneImageTransformService
{
    public bool HasCompleteTransform(Room room) =>
        room.SceneImageScaleXPercent is not null &&
        room.SceneImageScaleYPercent is not null &&
        room.SceneImagePanXPercent is not null &&
        room.SceneImagePanYPercent is not null;

    public bool IsValidTransform(Room room) =>
        room.SceneImageScaleXPercent is null &&
        room.SceneImageScaleYPercent is null &&
        room.SceneImagePanXPercent is null &&
        room.SceneImagePanYPercent is null ||
        room.SceneImageScaleXPercent is { } scaleX &&
        room.SceneImageScaleYPercent is { } scaleY &&
        room.SceneImagePanXPercent is { } panX &&
        room.SceneImagePanYPercent is { } panY &&
        double.IsFinite(scaleX) && scaleX > 0 &&
        double.IsFinite(scaleY) && scaleY > 0 &&
        double.IsFinite(panX) &&
        double.IsFinite(panY);

    public bool ShouldMarkStale(Room persistedRoom, Room draftRoom) =>
        HasCompleteTransform(persistedRoom) &&
        HasCompleteSceneDimensions(draftRoom) &&
        (persistedRoom.SceneUnitWidth != draftRoom.SceneUnitWidth || persistedRoom.SceneUnitHeight != draftRoom.SceneUnitHeight);

    private static bool HasCompleteSceneDimensions(Room room) =>
        room.SceneUnitWidth is { } width &&
        room.SceneUnitHeight is { } height &&
        double.IsFinite(width) && width > 0 &&
        double.IsFinite(height) && height > 0;
}
