using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapManifestReviewNavigationStateTests
{
    [Fact]
    public void OpenZoneAndBack_RetainTheExistingPlanSelections()
    {
        var alphaChange = Row(MapReconciliationKind.Changed, "Alpha", selected: false);
        var betaAddition = Row(MapReconciliationKind.Added, "Beta", selected: true);
        var plan = new MapReconciliationPlan
        {
            Manifest = new("Map", [], [], 0),
            Rows = [alphaChange, betaAddition]
        };
        var review = new MapManifestReviewState(plan);
        var navigation = new MapManifestReviewNavigationState();

        review.SetRowSelected(alphaChange, true);
        navigation.SetSearch("stale search");

        Assert.True(navigation.OpenZone("Alpha"));
        Assert.Equal("Alpha", navigation.FocusedZone);
        Assert.Equal(string.Empty, navigation.Search);
        Assert.True(alphaChange.Selected);
        Assert.True(betaAddition.Selected);

        navigation.BackToSummary();

        Assert.Null(navigation.FocusedZone);
        Assert.True(alphaChange.Selected);
        Assert.True(betaAddition.Selected);
    }

    [Fact]
    public void MoveZone_UsesDisplayedSummaryOrderAndDisablesAtBothEnds()
    {
        var plan = new MapReconciliationPlan
        {
            Manifest = new("Map", [], [], 0),
            Rows =
            [
                Row(MapReconciliationKind.Unchanged, "zulu", selected: false),
                Row(MapReconciliationKind.Changed, "beta", selected: false),
                Row(MapReconciliationKind.Unchanged, "Alpha", selected: false)
            ]
        };
        var review = new MapManifestReviewState(plan);
        var navigation = new MapManifestReviewNavigationState();
        var zones = review.Zones;

        Assert.Equal(["beta", "Alpha", "zulu"], zones.Select(zone => zone.ZoneInGameId));
        Assert.True(navigation.OpenZone("beta"));
        Assert.False(navigation.CanMoveZone(zones, -1));
        Assert.True(navigation.CanMoveZone(zones, 1));

        Assert.True(navigation.MoveZone(zones, 1));
        Assert.Equal("Alpha", navigation.FocusedZone);
        Assert.True(navigation.MoveZone(zones, 1));
        Assert.Equal("zulu", navigation.FocusedZone);
        Assert.True(navigation.CanMoveZone(zones, -1));
        Assert.False(navigation.CanMoveZone(zones, 1));
        Assert.False(navigation.MoveZone(zones, 1));
    }

    private static MapReconciliationPlanRow Row(MapReconciliationKind kind, string zone, bool selected) => new()
    {
        Kind = kind,
        Entity = MapReconciliationEntity.Zone,
        Label = zone,
        ZoneInGameId = zone,
        Selected = selected
    };
}
