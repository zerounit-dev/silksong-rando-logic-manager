namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapOverlayPlacement(double X, double Y, double Width, double Height);

public sealed class MapOverlayPlacementService
{
    public bool TryProject(double minX, double minY, double maxX, double maxY, double scaleXPercent, double scaleYPercent, double leftOffsetPercent, double bottomOffsetPercent, double aspectRatio, out MapOverlayPlacement placement)
    {
        placement = null!;
        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY) || maxX <= minX || maxY <= minY ||
            !double.IsFinite(scaleXPercent) || scaleXPercent <= 0 || !double.IsFinite(scaleYPercent) || scaleYPercent <= 0 || !double.IsFinite(leftOffsetPercent) || !double.IsFinite(bottomOffsetPercent) || !double.IsFinite(aspectRatio) || aspectRatio <= 0)
        {
            return false;
        }

        var mapWidth = maxX - minX;
        var mapHeight = maxY - minY;
        var height = mapHeight * scaleYPercent / 100;
        var width = mapHeight * aspectRatio * scaleXPercent / 100;
        var x = minX + mapWidth * leftOffsetPercent / 100;
        var bottom = minY + mapHeight * bottomOffsetPercent / 100;
        placement = new(x, maxY - bottom - height, width, height);
        return true;
    }
}
