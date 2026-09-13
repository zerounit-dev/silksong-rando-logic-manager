using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

/// <summary>Transient geometry comparison presentation for a focused review row.</summary>
public static class MapManifestReviewAreaPresentation
{
    public static MapManifestReviewAreaState Present(MapReconciliationPlanRow row)
    {
        if (row.Kind == MapReconciliationKind.Unchanged ||
            (row.CurrentBounds is null && row.ProposedBounds is null))
            return MapManifestReviewAreaState.Same;

        var current = Area(row.CurrentBounds);
        var proposed = Area(row.ProposedBounds);
        return proposed > current ? MapManifestReviewAreaState.Grew :
            proposed < current ? MapManifestReviewAreaState.Shrank :
            MapManifestReviewAreaState.Same;
    }

    private static double Area(MapUnitBounds? bounds) => bounds?.Area ?? 0;
}

public enum MapManifestReviewAreaState { Grew, Shrank, Same }
