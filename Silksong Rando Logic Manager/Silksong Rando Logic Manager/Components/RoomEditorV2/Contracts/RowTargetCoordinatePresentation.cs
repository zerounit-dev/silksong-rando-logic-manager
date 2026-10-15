namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

/// <summary>Saved numeric geometry only; independent of targeting admission and visibility.</summary>
public enum RowTargetCoordinateState { AnnotationUnavailable, GamePositionUnavailable, Displaced, AlignedOrNotApplicable }

public static class RowTargetCoordinatePresentation
{
    private static bool Finite(double? value) => value is double number && double.IsFinite(number);
    private static bool Pair(double? x, double? y) => Finite(x) && Finite(y);

    public static RowTargetCoordinateState Point(double? x, double? y) => Pair(x, y)
        ? RowTargetCoordinateState.AlignedOrNotApplicable : RowTargetCoordinateState.AnnotationUnavailable;

    public static RowTargetCoordinateState Frame(double? x, double? y, double? width, double? height) =>
        Pair(x, y) && Finite(width) && Finite(height) && width > 0 && height > 0
            ? RowTargetCoordinateState.AlignedOrNotApplicable : RowTargetCoordinateState.AnnotationUnavailable;

    public static RowTargetCoordinateState ComparedPoint(double? x, double? y, double? gameX, double? gameY) =>
        !Pair(x, y) ? RowTargetCoordinateState.AnnotationUnavailable
        : !Pair(gameX, gameY) ? RowTargetCoordinateState.GamePositionUnavailable
        : x == gameX && y == gameY ? RowTargetCoordinateState.AlignedOrNotApplicable
        : RowTargetCoordinateState.Displaced;

    public static string CssClass(RowTargetCoordinateState state) => state switch
    {
        RowTargetCoordinateState.AnnotationUnavailable => "row-target-annotation-unavailable",
        RowTargetCoordinateState.GamePositionUnavailable => "row-target-game-unavailable",
        RowTargetCoordinateState.Displaced => "row-target-displaced",
        _ => "row-target-aligned"
    };

    public static string Tooltip(string action, RowTargetCoordinateState state, bool enabled, bool gameApplicable) =>
        $"{action}; {state switch
        {
            RowTargetCoordinateState.AnnotationUnavailable => "saved annotation geometry is absent or invalid",
            RowTargetCoordinateState.GamePositionUnavailable => "saved game position is absent or invalid",
            RowTargetCoordinateState.Displaced => "saved annotation differs from game position",
            _ => gameApplicable ? "saved annotation matches game position" : "saved annotation geometry is valid; game comparison not applicable"
        }}; saved annotation visibility {(enabled ? "enabled" : "disabled")}";
}
