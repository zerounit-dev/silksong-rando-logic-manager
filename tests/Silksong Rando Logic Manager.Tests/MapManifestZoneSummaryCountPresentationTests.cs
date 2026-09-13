using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapManifestZoneSummaryCountPresentationTests
{
    [Theory]
    [InlineData(MapReconciliationKind.Added, "map-review-count-added")]
    [InlineData(MapReconciliationKind.Removed, "map-review-count-removed")]
    [InlineData(MapReconciliationKind.Changed, "map-review-count-changed")]
    [InlineData(MapReconciliationKind.Unchanged, "")]
    public void Present_NonzeroCount_RetainsTheNumberAndUsesTheSettledSemanticClass(MapReconciliationKind kind, string cssClass)
    {
        var presentation = MapManifestZoneSummaryCountPresentation.Present(kind, 12);

        Assert.Equal("12", presentation.Text);
        Assert.Equal(cssClass, presentation.CssClass);
    }

    [Theory]
    [InlineData(MapReconciliationKind.Added)]
    [InlineData(MapReconciliationKind.Removed)]
    [InlineData(MapReconciliationKind.Changed)]
    [InlineData(MapReconciliationKind.Unchanged)]
    public void Present_ZeroCount_UsesMutedDash(MapReconciliationKind kind)
    {
        var presentation = MapManifestZoneSummaryCountPresentation.Present(kind, 0);

        Assert.Equal("-", presentation.Text);
        Assert.Equal("map-review-count-muted", presentation.CssClass);
    }
}
