namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapOverlayAsset(string Key, string Url, double AspectRatio);

public sealed class MapOverlayAssetCatalog
{
    private static readonly IReadOnlyDictionary<string, MapOverlayAsset> Assets = new Dictionary<string, MapOverlayAsset>(StringComparer.Ordinal)
    {
        ["area-map-hd"] = new("area-map-hd", "/images/area-map-hd.png", 10d / 7d)
    };

    public bool TryGet(string? key, out MapOverlayAsset asset)
    {
        if (key is not null && Assets.TryGetValue(key, out asset!)) return true;
        asset = null!;
        return false;
    }
}
