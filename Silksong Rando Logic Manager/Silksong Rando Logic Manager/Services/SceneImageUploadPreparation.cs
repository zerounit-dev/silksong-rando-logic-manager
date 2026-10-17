namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Dialog-owned interpretation of a pending upload, never persisted or exchanged.
/// Mapping is in oriented input pixels; source-local correction text belongs to the page draft.</summary>
public sealed record SceneImageUploadPreparation(bool Native = true, string Resolution = "recommended", double? CustomTarget = null);

public sealed record SceneImagePreparedSize(uint Width, uint Height, double RatioX, double RatioY);

/// <summary>Pure upload-time math, shared by presentation and final service admission.</summary>
public static class SceneImageUploadMath
{
    public static double? RecommendedTarget(double minX, double minY, double? maxX, double? maxY)
    {
        if (!SceneBounds.TryGetSize(minX, minY, maxX, maxY, out var w, out var h)) return null;
        var longest = Math.Max(w, h);
        return longest <= 40 ? 64 : longest <= 80 ? 32 : longest <= 160 ? 16 : 8;
    }

    public static SceneImagePreparedSize Calculate(uint width, uint height, double? px, double? py,
        SceneImageUploadPreparation preparation, double minX, double minY, double? maxX, double? maxY, bool enforceWebPLimit = true)
    {
        if (width == 0 || height == 0 || !SceneBounds.HasUsableScales(px, py))
            throw new InvalidOperationException("Enter positive finite input pixels per unit for both axes.");
        var target = preparation.Resolution switch
        {
            "recommended" => RecommendedTarget(minX, minY, maxX, maxY),
            "keep" => 1d,
            "32" => 32d, "16" => 16d, "8" => 8d,
            "custom" => preparation.CustomTarget,
            _ => throw new InvalidOperationException("Choose a supported upload resolution.")
        };
        if (target is not > 0 || !double.IsFinite(target.Value))
            throw new InvalidOperationException(preparation.Resolution == "recommended"
                ? "Set scene bounds to calculate recommended resolution."
                : "Enter a positive finite target pixels per unit.");
        var ratio = preparation.Resolution == "keep" ? 1 : Math.Min(1, Math.Min(target.Value / px!.Value, target.Value / py!.Value));
        var w = Math.Max(1, Math.Round(width * ratio, MidpointRounding.ToEven));
        var h = Math.Max(1, Math.Round(height * ratio, MidpointRounding.ToEven));
        if (enforceWebPLimit && (w > 16383 || h > 16383))
            throw new InvalidOperationException("The prepared image exceeds WebP's 16383-pixel axis limit. Choose a lower resolution and retry.");
        return new((uint)w, (uint)h, w / width, h / height);
    }
}
