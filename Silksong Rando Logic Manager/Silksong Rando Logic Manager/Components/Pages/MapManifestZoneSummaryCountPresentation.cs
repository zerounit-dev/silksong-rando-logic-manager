using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

/// <summary>Compact semantic text presentation for zone-summary reconciliation counts.</summary>
public static class MapManifestZoneSummaryCountPresentation
{
    public static MapManifestZoneSummaryCount Present(MapReconciliationKind kind, int count) => count == 0
        ? new("-", "map-review-count-muted")
        : new(count.ToString(System.Globalization.CultureInfo.InvariantCulture), kind switch
        {
            MapReconciliationKind.Added => "map-review-count-added",
            MapReconciliationKind.Removed => "map-review-count-removed",
            MapReconciliationKind.Changed => "map-review-count-changed",
            _ => string.Empty
        });
}

public sealed record MapManifestZoneSummaryCount(string Text, string CssClass);
