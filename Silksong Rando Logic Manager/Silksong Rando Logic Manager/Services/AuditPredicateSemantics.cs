using System.Collections.Frozen;

namespace Silksong_Rando_Logic_Manager.Services;

internal enum AuditBindingKind
{
    Manual, Free, Impassable, Ignored, Sprint, Directional, Movement, Kit,
    Scuttlebrace, ShamanScuttler, DrillCrystalPogo, Technique, SoulTechnique,
    CrestPogo, NeedleStrike, Needle, Stall, Soul, Barnak, Item, Dependency, Setting, Act
}
internal sealed record AuditPredicateSemantic(AuditBindingKind Kind, string Key = "", int Value = 0);
internal sealed record AuditPredicateDefinitionPlan(AuditPredicateDefinition Definition, int CatalogueOrder,
    AuditPredicateSemantic Semantic);

internal static class AuditPredicateSemantics
{
    private static readonly FrozenDictionary<string, AuditPredicateSemantic> Signatures = Build();
    internal static AuditPredicateSemantic Classify(string signature) => Signatures.TryGetValue(signature, out var meaning)
        ? meaning : new(AuditBindingKind.Manual);

    private static FrozenDictionary<string, AuditPredicateSemantic> Build()
    {
        var table = new Dictionary<string, AuditPredicateSemantic>(StringComparer.Ordinal);
        void Add(string signature, AuditBindingKind kind, string key = "", int value = 0) => table.Add(signature, new(kind, key, value));
        foreach (var name in new[] { "sprint", "dash", "ledge-grab", "cling-grip", "clawline" })
            foreach (var suffix in new[] { "", ":{direction}", ":{quantity}", ":{direction}:{quantity}" })
                Add($"capability:{name}{suffix}", name == "sprint" ? AuditBindingKind.Sprint : AuditBindingKind.Directional, name);
        foreach (var name in new[] { "swim", "drifters-cloak", "faydown-cloak", "silk-soar", "heal" })
            foreach (var suffix in new[] { "", ":{quantity}" }) Add($"capability:{name}{suffix}", AuditBindingKind.Movement, name);
        foreach (var name in new[] { "silkspear", "sharpdart", "thread-storm", "rune-rage" })
            foreach (var suffix in new[] { "", ":{quantity}" }) Add($"capability:{name}{suffix}", AuditBindingKind.Kit, name);
        foreach (var name in new[] { "think", "look", "move", "jump", "taunt", "naked-pogo" })
            foreach (var suffix in new[] { "", ":{direction}", ":{difficulty}" }) Add($"capability:{name}{suffix}", AuditBindingKind.Ignored, name);
        foreach (var name in new[] { "break-vines", "break-wall", "break-blast-rock", "break-airlock", "break-switch" })
            Add($"capability:{name}:{{direction}}", AuditBindingKind.Needle, name);
        foreach (var (name, kind) in new[] { ("scuttlebrace", AuditBindingKind.Scuttlebrace), ("shaman-scuttler", AuditBindingKind.ShamanScuttler),
            ("drill-crystal-pogo", AuditBindingKind.DrillCrystalPogo) })
            foreach (var suffix in new[] { ":{difficulty}", ":{difficulty}:{quantity}" }) Add($"capability:{name}{suffix}", kind, name);
        foreach (var name in new[] { "wind-skip", "cocoon-skip", "damage-boost", "hazard-respawn", "enemy-pogo" })
            foreach (var suffix in new[] { ":{difficulty}", ":{difficulty}:{quantity}" }) Add($"capability:{name}{suffix}", AuditBindingKind.Technique, name);
        Add("capability:enemy-type-pogo:{soul}:{difficulty}", AuditBindingKind.SoulTechnique, "enemy-pogo");
        Add("technique:damage-boost:{soul}:{difficulty}", AuditBindingKind.SoulTechnique, "damage-boost");
        Add("capability:crest-pogo:{crest}:{difficulty}", AuditBindingKind.CrestPogo);
        Add("capability:needle-strike:{crest}:{difficulty}", AuditBindingKind.NeedleStrike);
        Add("capability:needle:{crest}:{direction}", AuditBindingKind.Needle, "needle");
        foreach (var suffix in new[] { "", ":{quantity}" }) Add("capability:stall:{method}:{difficulty}" + suffix, AuditBindingKind.Stall);
        Add("soul:{soul}", AuditBindingKind.Soul); Add("macro:barnak-elevator", AuditBindingKind.Barnak);
        Add("item:{item}", AuditBindingKind.Item); Add("dependency:{check}", AuditBindingKind.Dependency);
        Add("none", AuditBindingKind.Free); Add("invalid", AuditBindingKind.Impassable); Add("impassable", AuditBindingKind.Impassable);
        foreach (var setting in new[] { "steel-soul", "bellshrinesanity", "bossanity", "wishsanity", "entrance-randomizer", "advanced-knowledge" })
            foreach (var on in new[] { true, false }) Add($"setting:{(setting == "advanced-knowledge" ? "elite-ball-knowledge" : setting)}-{(on ? "on" : "off")}", AuditBindingKind.Setting, setting, on ? 1 : 0);
        for (var act = 1; act <= 3; act++) Add($"state:act-{act}", AuditBindingKind.Act, "act", 1 << (act - 1));
        Add("state:enemy-spawn-normal-world", AuditBindingKind.Act, "normal-world", 3);
        Add("state:enemy-spawn-black-thread-world", AuditBindingKind.Act, "black-thread", 4);
        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
