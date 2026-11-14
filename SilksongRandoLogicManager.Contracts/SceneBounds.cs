namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Usability is separate from lossless finite raw catalogue admission.</summary>
public static class SceneBounds
{
    public static bool TryGetSize(double minX, double minY, double? maxX, double? maxY, out double width, out double height)
    {
        width = maxX.GetValueOrDefault() - minX;
        height = maxY.GetValueOrDefault() - minY;
        return double.IsFinite(minX) && double.IsFinite(minY) && maxX is { } x && maxY is { } y &&
            double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
    }
    public static bool HasUsableScales(double? x, double? y) => x is > 0 && y is > 0 && double.IsFinite(x.Value) && double.IsFinite(y.Value);
    public static bool HasUsablePixelOffsets(double? x, double? y) => x is { } px && y is { } py && double.IsFinite(px) && double.IsFinite(py);
    public static bool FiniteOrNull(double? value) => value is null || double.IsFinite(value.Value);
    public static (double? ScaleX, double? ScaleY, double? PixelOffsetX, double? PixelOffsetY) ConvertLegacy(
        double? width, double? height, double? coverageX, double? coverageY, double? panX, double? panY)
    {
        if (coverageX is null && coverageY is null && panX is null && panY is null) return (null, null, null, null);
        if (TryGetSize(0, 0, width, height, out var w, out var h) && HasUsableScales(coverageX, coverageY) && HasUsablePixelOffsets(panX, panY))
        {
            var sx = 20500d * coverageX!.Value / (100d * w);
            var sy = 15000d * coverageY!.Value / (100d * h);
            var px = 20500d * ((50d - panX!.Value) / 100d - coverageX.Value / 200d);
            var py = 15000d * ((50d + panY!.Value) / 100d - coverageY.Value / 200d);
            if (HasUsableScales(sx, sy) && HasUsablePixelOffsets(px, py)) return (sx, sy, px, py);
        }
        return (null, null, null, null);
    }
}
