using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

/// <summary>Maps review bounds to the padded SVG viewport without changing map-unit geometry.</summary>
public sealed record MapManifestReviewViewport(double MinX, double MinY, double MaxX, double MaxY)
{
    public string ViewBox => FormattableString.Invariant($"{MinX} {MinY} {MaxX - MinX} {MaxY - MinY}");
    public string MapUnitToSvgTransform => FormattableString.Invariant($"translate(0 {MinY + MaxY}) scale(1 -1)");

    public static MapManifestReviewViewport FromBounds(IEnumerable<MapUnitBounds> bounds)
    {
        var available = bounds.Where(x => x.IsAvailable).ToList();
        if (available.Count == 0) return new(0, 0, 1, 1);

        var minX = available.Min(x => x.MinX!.Value);
        var minY = available.Min(x => x.MinY!.Value);
        var maxX = available.Max(x => x.MaxX!.Value);
        var maxY = available.Max(x => x.MaxY!.Value);
        var padX = Math.Max((maxX - minX) * .03, .1);
        var padY = Math.Max((maxY - minY) * .03, .1);
        return new(minX - padX, minY - padY, maxX + padX, maxY + padY);
    }
}
