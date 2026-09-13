using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapOverlayAssetCatalogTests
{
    [Fact]
    public void Catalog_AllowsOnlyTheConfiguredAreaMapAsset()
    {
        var catalog = new MapOverlayAssetCatalog();

        Assert.True(catalog.TryGet("area-map-hd", out var asset));
        Assert.Equal("/images/area-map-hd.png", asset.Url);
        Assert.Equal(10d / 7d, asset.AspectRatio);
        Assert.False(catalog.TryGet("C:/untrusted.png", out _));
    }
}
