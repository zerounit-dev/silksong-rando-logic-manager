using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

/// <summary>Pure modal presentation from the applied active tables, independent of live annotations.</summary>
internal static class SceneImageCapturePreviewProjection
{
    internal static V2SceneImageCapturePreviewView Create(IEnumerable<TransitionRowView> transitions,
        IEnumerable<CheckRowView> checks, SceneViewportBounds? roomBounds, SceneImageCaptureDraft draft)
    {
        var markers = transitions.Where(x => CompletePair(x.InGamePositionX, x.InGamePositionY))
            .Select(x => new V2SceneImageCaptureMarkerView("exit", x.Alias, x.InGamePositionX!.Value, x.InGamePositionY!.Value))
            .Concat(checks.Where(x => CompletePair(x.InGamePositionX, x.InGamePositionY))
                .Select(x => new V2SceneImageCaptureMarkerView("check", x.FriendlyName, x.InGamePositionX!.Value, x.InGamePositionY!.Value,
                    CheckLocationTypeCatalogue.IsEnemy(x.LocationType))))
            .OrderBy(x => x.Kind).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(InitialViewport(markers, roomBounds, draft), markers);
    }

    private static bool CompletePair(double? x, double? y) =>
        x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b);

    private static SceneViewportBounds InitialViewport(IReadOnlyList<V2SceneImageCaptureMarkerView> markers,
        SceneViewportBounds? roomBounds, SceneImageCaptureDraft draft)
    {
        // Retain the existing origin/minimum/maximum and effective-room references,
        // but never admit live annotation anchors or frame extents to this camera.
        var minX = Math.Min(0, roomBounds?.X ?? 0);
        var minY = Math.Min(0, roomBounds?.Y ?? 0);
        var maxX = Math.Max(0, roomBounds is { } boundsX ? boundsX.X + boundsX.Width : 0);
        var maxY = Math.Max(0, roomBounds is { } boundsY ? boundsY.Y + boundsY.Height : 0);
        void Include(double? x, double? y)
        {
            if (!CompletePair(x, y)) return;
            minX = Math.Min(minX, x!.Value); minY = Math.Min(minY, y!.Value);
            maxX = Math.Max(maxX, x.Value); maxY = Math.Max(maxY, y.Value);
        }
        Include(draft.MinX, draft.MinY);
        Include(draft.MaxX ?? 0, draft.MaxY ?? 0);
        foreach (var marker in markers) Include(marker.X, marker.Y);
        // Match the established scene inspection normalization and ten-percent padding.
        var width = maxX - minX; var height = maxY - minY;
        var minimum = Math.Max(1d, Math.Max(width, height) * SceneViewportMath.Padding);
        var normalizedWidth = Math.Max(width, minimum); var normalizedHeight = Math.Max(height, minimum);
        minX -= (normalizedWidth - width) / 2d; minY -= (normalizedHeight - height) / 2d;
        var padding = SceneViewportMath.Padding;
        return new(minX - normalizedWidth * padding, -(minY + normalizedHeight * (1d + padding)),
            normalizedWidth * (1d + 2d * padding), normalizedHeight * (1d + 2d * padding));
    }
}
