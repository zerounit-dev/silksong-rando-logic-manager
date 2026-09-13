using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapOverlayPlacementServiceTests
{
    [Fact]
    public void Project_UsesMapFramePercentagesAndSvgBottomOrigin()
    {
        var service = new MapOverlayPlacementService();

        Assert.True(service.TryProject(10, 20, 110, 70, 100, 100, 10, 20, 10d / 7d, out var placement));

        Assert.Equal(20, placement.X);
        Assert.Equal(10d / 7d * 50, placement.Width);
        Assert.Equal(50, placement.Height);
        Assert.Equal(-10, placement.Y);
    }

    [Fact]
    public void Project_AllowsIndependentHorizontalAndVerticalScale()
    {
        var service = new MapOverlayPlacementService();

        Assert.True(service.TryProject(0, 0, 100, 50, 50, 200, 0, 0, 10d / 7d, out var placement));

        Assert.Equal(250d / 7d, placement.Width);
        Assert.Equal(100, placement.Height);
    }

    [Theory]
    [InlineData(0, 0, 10, 10, 0, 100, 0, 0, 1)]
    [InlineData(0, 0, 10, 10, 100, -1, 0, 0, 1)]
    [InlineData(0, 0, 10, 10, 100, 100, 0, 0, 0)]
    public void Project_RejectsInvalidGeometry(double minX, double minY, double maxX, double maxY, double scaleX, double scaleY, double left, double bottom, double aspect)
    {
        Assert.False(new MapOverlayPlacementService().TryProject(minX, minY, maxX, maxY, scaleX, scaleY, left, bottom, aspect, out _));
    }
}
