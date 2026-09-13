using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapManifestReviewAreaPresentationTests
{
    [Theory]
    [MemberData(nameof(AreaCases))]
    public void Present_DescribesUsableGeometryChanges(MapReconciliationKind kind, MapUnitBounds? current, MapUnitBounds? proposed, MapManifestReviewAreaState expected)
    {
        var row = new MapReconciliationPlanRow { Kind = kind, Entity = MapReconciliationEntity.Chunk, Label = "chunk", CurrentBounds = current, ProposedBounds = proposed };

        Assert.Equal(expected, MapManifestReviewAreaPresentation.Present(row));
    }

    public static IEnumerable<object?[]> AreaCases()
    {
        yield return [MapReconciliationKind.Changed, new MapUnitBounds(0, 0, 2, 2), new MapUnitBounds(0, 0, 3, 2), MapManifestReviewAreaState.Grew];
        yield return [MapReconciliationKind.Changed, new MapUnitBounds(0, 0, 3, 2), new MapUnitBounds(0, 0, 2, 2), MapManifestReviewAreaState.Shrank];
        yield return [MapReconciliationKind.Changed, new MapUnitBounds(0, 0, 2, 2), new MapUnitBounds(1, 1, 3, 3), MapManifestReviewAreaState.Same];
        yield return [MapReconciliationKind.Added, MapUnitBounds.Unavailable, new MapUnitBounds(0, 0, 2, 2), MapManifestReviewAreaState.Grew];
        yield return [MapReconciliationKind.Removed, new MapUnitBounds(0, 0, 2, 2), MapUnitBounds.Unavailable, MapManifestReviewAreaState.Shrank];
        yield return [MapReconciliationKind.Unchanged, new MapUnitBounds(0, 0, 2, 2), new MapUnitBounds(0, 0, 3, 3), MapManifestReviewAreaState.Same];
        yield return [MapReconciliationKind.Changed, null, null, MapManifestReviewAreaState.Same];
    }
}
