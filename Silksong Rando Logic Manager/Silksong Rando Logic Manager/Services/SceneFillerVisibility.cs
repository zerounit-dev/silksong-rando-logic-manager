namespace Silksong_Rando_Logic_Manager.Services;

// Application-owned, transient tree evidence. Never reruns actor classification
// or alters import instructions, names, Show IDs, or spatial projections.
public static class SceneFillerVisibility
{
    private static readonly HashSet<string> Anchors = new(StringComparer.Ordinal)
    { "UnityEngine.SpriteRenderer", "UnityEngine.MeshRenderer", "UnityEngine.ParticleSystem", "UnityEngine.AudioSource", "tk2dSprite" };
    private static readonly HashSet<string> Support = new(StringComparer.Ordinal)
    {
        "UnityEngine.Transform", "UnityEngine.MeshFilter", "UnityEngine.ParticleSystemRenderer",
        "SetZ", "SetZMin", "SetZRandom", "SetParticleScale", "KeepWorldScalePositive",
        "ParticleSystemAutoRecycle", "AudioSourceGamePause", "RandomAudioStartPosition",
        "AudioLoopRandom", "AudioEffectTag", "AudioPlayParticleCollision", "ThreadIllumination",
        "TeamCherry.SharedUtils.SetMaterialPropertyBlocks"
    };
    private static readonly HashSet<string> GrassRoot = new(StringComparer.Ordinal)
    { "GrassBehaviour", "GrassCut", "UnityEngine.Transform", "UnityEngine.AudioSource", "UnityEngine.BoxCollider2D" };
    private static readonly HashSet<string> GrassWind = new(StringComparer.Ordinal)
    { "UnityEngine.Transform", "GrassWind", "UnityEngine.BoxCollider2D" };

    public static void Apply(SceneDumpReview review)
    {
        foreach (var root in review.RootObjects) Apply(root);
    }
    private static void Apply(SceneDumpObject node)
    {
        foreach (var child in node.Children) Apply(child);
        node.HasKeptDescendant = node.Children.Any(child => !child.IsFiller || child.HasKeptDescendant);
        node.IsFiller = !SceneDumpIdentity.IsImportable(node.Classification) && node.HasReliableFillerEvidence &&
            node.Components.All(component => component.HasReliableFillerEvidence) &&
            (PresentationOnly(node) || CosmeticGrass(node) || CosmeticWind(node) ||
             (node.Children.Count > 0 && !node.HasKeptDescendant && node.Components.All(component => component.Type == "UnityEngine.Transform")));
    }
    private static bool PresentationOnly(SceneDumpObject node) =>
        node.Components.Any(component => Anchors.Contains(component.Type)) &&
        node.Components.All(component => Anchors.Contains(component.Type) || Support.Contains(component.Type) ||
            (component.Type == "BasicSpriteAnimator" && component.ConditionalFillerEligible));
    private static bool CosmeticGrass(SceneDumpObject node) =>
        node.Components.Any(component => component.Type == "GrassBehaviour" && component.ConditionalFillerEligible) &&
        node.Components.Any(component => component.Type == "GrassCut" && component.ConditionalFillerEligible) &&
        node.Components.All(component => GrassRoot.Contains(component.Type) &&
            (component.Type is not ("GrassBehaviour" or "GrassCut") || component.ConditionalFillerEligible));
    private static bool CosmeticWind(SceneDumpObject node) =>
        node.ParentId is not null &&
        node.Components.Any(component => component.Type == "UnityEngine.Transform") &&
        node.Components.Any(component => component.Type == "GrassWind" && component.ConditionalFillerEligible) &&
        node.Components.All(component => GrassWind.Contains(component.Type) &&
            (component.Type != "GrassWind" || component.ConditionalFillerEligible));
}
