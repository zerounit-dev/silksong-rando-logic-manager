using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

/// <summary>Pure typed scene-canvas navigation geometry; output coordinates are SVG Y-down.</summary>
public static class SceneViewportMath
{
    public const double Padding = .10d;
    public const double MaximumZoomIn = 8d;
    public const double WheelExponentPerPixel = .003d;
    public const double MaximumWheelExponent = .06d;
    public static SceneViewportBounds Initial(SceneLayoutView view)
    {
        SceneViewportBounds? content = view.HasValidBounds && view.SceneUnitWidth is > 0 and var width && view.SceneUnitHeight is > 0 and var height ? new(0, 0, width, height) : null;
        foreach (var frame in view.Frames.Where(IsFinite)) content = Include(content, new(frame.X, frame.Y, frame.Width, frame.Height));
        foreach (var marker in view.Markers.Where(IsFinite)) content = Include(content, new(marker.X, marker.Y, 0, 0));
        var normalized = Normalize(content ?? new SceneViewportBounds(-.5d, -.5d, 1d, 1d));
        var padded = new SceneViewportBounds(normalized.X - normalized.Width * Padding, normalized.Y - normalized.Height * Padding, normalized.Width * (1d + 2d * Padding), normalized.Height * (1d + 2d * Padding));
        return new SceneViewportBounds(padded.X, -(padded.Y + padded.Height), padded.Width, padded.Height);
    }
    public static double WheelFactor(double deltaPixels) => Math.Exp(Math.Clamp(deltaPixels * WheelExponentPerPixel, -MaximumWheelExponent, MaximumWheelExponent));
    public static SceneViewportBounds Zoom(SceneViewportBounds current, SceneViewportBounds initial, double pointerX, double pointerY, double deltaPixels)
    {
        var width = Math.Clamp(current.Width * WheelFactor(deltaPixels), initial.Width / MaximumZoomIn, initial.Width); var factor = width / current.Width;
        return Clamp(new(pointerX - (pointerX - current.X) * factor, pointerY - (pointerY - current.Y) * factor, width, width * initial.Height / initial.Width), initial);
    }
    public static SceneViewportBounds Pan(SceneViewportBounds current, SceneViewportBounds initial, double deltaX, double deltaY) => Clamp(new(current.X - deltaX, current.Y - deltaY, current.Width, current.Height), initial);
    public static SceneViewportBounds Clamp(SceneViewportBounds view, SceneViewportBounds initial)
    {
        var width = Math.Clamp(view.Width, initial.Width / MaximumZoomIn, initial.Width); var height = width * initial.Height / initial.Width;
        return new SceneViewportBounds(Math.Clamp(view.X, initial.X, initial.X + initial.Width - width), Math.Clamp(view.Y, initial.Y, initial.Y + initial.Height - height), width, height);
    }
    private static bool IsFinite(SceneSubroomFrameView frame) => double.IsFinite(frame.X) && double.IsFinite(frame.Y) && double.IsFinite(frame.Width) && double.IsFinite(frame.Height) && frame.Width > 0 && frame.Height > 0;
    private static bool IsFinite(SceneMarkerView marker) => double.IsFinite(marker.X) && double.IsFinite(marker.Y);
    private static SceneViewportBounds Include(SceneViewportBounds? current, SceneViewportBounds next)
    { if (current is null) return next; var x = Math.Min(current.X, next.X); var y = Math.Min(current.Y, next.Y); return new(x, y, Math.Max(current.X + current.Width, next.X + next.Width) - x, Math.Max(current.Y + current.Height, next.Y + next.Height) - y); }
    private static SceneViewportBounds Normalize(SceneViewportBounds value)
    { var minimum = Math.Max(1d, Math.Max(value.Width, value.Height) * Padding); var width = Math.Max(value.Width, minimum); var height = Math.Max(value.Height, minimum); return new(value.X - (width - value.Width) / 2d, value.Y - (height - value.Height) / 2d, width, height); }
}
public sealed record SceneViewportBounds(double X, double Y, double Width, double Height);
