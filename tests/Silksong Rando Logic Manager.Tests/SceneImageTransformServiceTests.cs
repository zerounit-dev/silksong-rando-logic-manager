using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SceneImageTransformServiceTests
{
    private readonly SceneImageTransformService service = new();

    [Fact]
    public void TransformValidation_RequiresAllFiniteValuesAndPositiveScales()
    {
        Assert.True(service.IsValidTransform(new Room()));
        Assert.True(service.IsValidTransform(TransformRoom()));
        Assert.False(service.IsValidTransform(new Room { SceneImageScaleXPercent = 100 }));
        Assert.False(service.IsValidTransform(new Room { SceneImageScaleXPercent = 0, SceneImageScaleYPercent = 100, SceneImagePanXPercent = 0, SceneImagePanYPercent = 0 }));
        Assert.False(service.IsValidTransform(new Room { SceneImageScaleXPercent = 100, SceneImageScaleYPercent = 100, SceneImagePanXPercent = double.NaN, SceneImagePanYPercent = 0 }));
    }

    [Fact]
    public void StaleState_RequiresExistingTransformAndChangedCompleteDimensions()
    {
        var captured = TransformRoom(width: 10, height: 20);

        Assert.True(service.ShouldMarkStale(captured, TransformRoom(width: 11, height: 20)));
        Assert.False(service.ShouldMarkStale(captured, TransformRoom(width: 10, height: 20)));
        Assert.False(service.ShouldMarkStale(captured, TransformRoom(width: 11, height: null)));
        Assert.False(service.ShouldMarkStale(new Room { SceneUnitWidth = 10, SceneUnitHeight = 20 }, TransformRoom(width: 11, height: 20)));
    }

    private static Room TransformRoom(double? width = null, double? height = null) => new()
    {
        SceneUnitWidth = width,
        SceneUnitHeight = height,
        SceneImageScaleXPercent = 100,
        SceneImageScaleYPercent = 100,
        SceneImagePanXPercent = 0,
        SceneImagePanYPercent = 0
    };
}
