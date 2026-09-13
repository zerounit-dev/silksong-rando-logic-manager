using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

public static class MapManifestReviewPreviewPresentation
{
    public static string CssClass(MapManifestPreviewShape shape) =>
        $"map-review-{(shape.IsCurrent ? "current" : shape.Kind.ToString().ToLowerInvariant())} " +
        $"{(shape.IsCurrent && shape.Kind == MapReconciliationKind.Changed ? "map-review-changed-prior" : "")} " +
        $"{(shape.IsSelectable && shape.Layer == "proposal" && !shape.Selected ? "map-review-deferred" : "")}";
}
