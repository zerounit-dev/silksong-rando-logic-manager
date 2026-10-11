using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneImageTransformService
{
    public bool HasCompleteTransform(Room r) => SceneBounds.HasUsableScales(r.SceneImagePixelsPerUnitX, r.SceneImagePixelsPerUnitY) &&
        (!r.UseSceneImage || SceneBounds.HasUsablePixelOffsets(r.SceneImagePixelOffsetX, r.SceneImagePixelOffsetY));
    public bool IsValidTransform(Room r) => SceneBounds.FiniteOrNull(r.SceneImagePixelsPerUnitX) && SceneBounds.FiniteOrNull(r.SceneImagePixelsPerUnitY) &&
        SceneBounds.FiniteOrNull(r.SceneImagePixelOffsetX) && SceneBounds.FiniteOrNull(r.SceneImagePixelOffsetY);
    public bool ShouldMarkStale(Room before, Room after) => before.SceneMinX != after.SceneMinX || before.SceneMinY != after.SceneMinY ||
        before.SceneMaxX != after.SceneMaxX || before.SceneMaxY != after.SceneMaxY;
}
