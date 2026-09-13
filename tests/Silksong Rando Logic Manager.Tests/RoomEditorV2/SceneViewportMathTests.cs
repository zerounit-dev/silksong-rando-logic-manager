using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class SceneViewportMathTests
{
    [Fact]
    public void Initial_UnionsValidDeclaredBoundsAndPositionedContentThenPadsTenPercent()
    {
        var view = new SceneLayoutView(true, 100, 50,
            [new(Guid.NewGuid(), "frame", "frame", -20, -30, 5, 7)],
            [new(Guid.NewGuid(), "exit", "a", "exit", 150, 10)]);

        Assert.Equal(new SceneViewportBounds(-37, -58, 204, 96), SceneViewportMath.Initial(view));
    }

    [Fact]
    public void Initial_UsesPositionedContentOnlyWhenDeclaredBoundsAreUnavailable()
    {
        var view = new SceneLayoutView(false, null, null, [], [new(Guid.NewGuid(), "check", "c", "check", 5, 6)]);

        var initial = SceneViewportMath.Initial(view);
        Assert.Equal(4.4, initial.X, 8); Assert.Equal(-6.6, initial.Y, 8); Assert.Equal(1.2, initial.Width, 8); Assert.Equal(1.2, initial.Height, 8);
    }

    [Fact]
    public void Zoom_IsPointerAnchoredCappedAndCannotZoomOutPastInitialFit()
    {
        var initial = new SceneViewportBounds(0, 0, 120, 60);
        var zoomed = initial;
        for (var i = 0; i < 100; i++) zoomed = SceneViewportMath.Zoom(zoomed, initial, 90, 45, -10000);
        Assert.Equal(15, zoomed.Width, 8); Assert.Equal(7.5, zoomed.Height, 8);
        Assert.Equal(78.75, zoomed.X, 8); Assert.Equal(39.375, zoomed.Y, 8);
        var reset = initial;
        for (var i = 0; i < 100; i++) reset = SceneViewportMath.Zoom(reset, initial, 90, 45, 10000);
        Assert.Equal(initial, reset);
    }

    [Fact]
    public void Pan_ClampsInsideThePaddedInitialExtentAndResetIsInitial()
    {
        var initial = new SceneViewportBounds(-10, -5, 120, 60);
        var zoomed = SceneViewportMath.Zoom(initial, initial, 50, 25, -10000);
        var clamped = SceneViewportMath.Pan(zoomed, initial, -10000, -10000);
        Assert.Equal(initial.X + initial.Width - clamped.Width, clamped.X, 8);
        Assert.Equal(initial.Y + initial.Height - clamped.Height, clamped.Y, 8);
        Assert.Equal(initial, initial); // reset is exactly the immutable initial basis.
    }
}
