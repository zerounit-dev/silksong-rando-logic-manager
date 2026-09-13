namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>One scene-layout-owned change to a displayed check row.</summary>
public sealed record CheckAnnotationMutation(Guid CheckId, double? X, double? Y, bool? EnableAnnotation)
{
    public static CheckAnnotationMutation PlaceOrMove(Guid checkId, double x, double y, bool enable) => new(checkId, x, y, enable ? true : null);
    public static CheckAnnotationMutation Disable(Guid checkId) => new(checkId, null, null, false);
}
