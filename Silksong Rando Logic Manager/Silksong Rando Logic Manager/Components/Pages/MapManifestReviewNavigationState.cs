using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

/// <summary>Transient focused-zone context for one map-manifest review modal.</summary>
public sealed class MapManifestReviewNavigationState
{
    public string? FocusedZone { get; private set; }
    public string Search { get; private set; } = string.Empty;
    public MapReconciliationKind? ChangeFilter { get; private set; }
    public Guid? HoveredRow { get; set; }

    public bool OpenZone(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone)) return false;

        FocusedZone = zone;
        Search = string.Empty;
        ChangeFilter = null;
        return true;
    }

    public void SetSearch(string? search) => Search = search ?? string.Empty;
    public void SetChangeFilter(MapReconciliationKind? filter) => ChangeFilter = filter;

    /// <summary>Moves through the already-presented summary order without changing plan state.</summary>
    public bool MoveZone(IReadOnlyList<MapManifestZoneSummary> zones, int offset)
    {
        if (FocusedZone is null || offset == 0) return false;

        var index = zones.ToList().FindIndex(zone => string.Equals(zone.ZoneInGameId, FocusedZone, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= zones.Count) return false;

        return OpenZone(zones[target].ZoneInGameId);
    }

    public bool CanMoveZone(IReadOnlyList<MapManifestZoneSummary> zones, int offset)
    {
        if (FocusedZone is null || offset == 0) return false;

        var index = zones.ToList().FindIndex(zone => string.Equals(zone.ZoneInGameId, FocusedZone, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + offset >= 0 && index + offset < zones.Count;
    }

    public void BackToSummary()
    {
        FocusedZone = null;
        Search = string.Empty;
        ChangeFilter = null;
        HoveredRow = null;
    }
}
