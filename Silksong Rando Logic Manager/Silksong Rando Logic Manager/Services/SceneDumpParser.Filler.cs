namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class SceneDumpParser
{
    // The ordinary streaming semantic scan also checks these finite owned shapes.
    // No second parse, raw JSON, references, frames or callbacks are retained.
    private sealed class FillerDataEvidence
    {
        public bool DataPresent, ObjectData, Invalid;
        public string? DataType;
        public readonly HashSet<string> Fields = new(StringComparer.Ordinal);
        public readonly HashSet<string> Valid = new(StringComparer.Ordinal);

        public bool Eligible(string type)
        {
            if (!DataPresent || !ObjectData || Invalid || DataType != type) return false;
            var allowed = type switch
            {
                "BasicSpriteAnimator" => AnimatorFields,
                "GrassBehaviour" => GrassFields,
                "GrassCut" => CutFields,
                "GrassWind" => WindFields,
                _ => null
            };
            if (allowed is null || !Fields.IsSubsetOf(allowed) || !Fields.IsSubsetOf(Valid)) return false;
            return type switch
            {
                "BasicSpriteAnimator" => Valid.Contains("OnAnimStart") && Valid.Contains("OnAnimEnd"),
                "GrassCut" => CutRequired.All(Valid.Contains),
                "GrassBehaviour" => GrassFields.IsSubsetOf(Valid),
                "GrassWind" => true,
                _ => false
            };
        }
    }
    private static readonly HashSet<string> AnimatorFields = new(StringComparer.Ordinal)
        { "$type", "fps", "startDelay", "preFrames", "frames", "startRandom", "looping", "playOnEnable", "visibilityCulling", "OnAnimStart", "OnAnimEnd" };
    private static readonly HashSet<string> GrassFields = new(StringComparer.Ordinal)
        { "$type", "walkReactAmount", "walkReactCurve", "walkReactLength", "attackReactAmount", "attackReactCurve", "attackReactLength", "disableSizeMultiplier", "pushAnim", "pushSounds", "cutSounds" };
    private static readonly HashSet<string> CutFields = new(StringComparer.Ordinal)
        { "$type", "disable", "enable", "disableColliders", "enableColliders", "enableGameObjects", "particles", "cutEffectPrefab", "callOnChildren", "persistent", "OnCut" };
    private static readonly HashSet<string> WindFields = new(StringComparer.Ordinal) { "$type" };
    private static readonly string[] CutRequired = ["persistent", "disableColliders", "enableColliders", "enableGameObjects", "OnCut", "callOnChildren", "disable", "enable"];

    private static bool IsMissingEvidence(string? value) => value is not null &&
        (value.StartsWith("<error:", StringComparison.Ordinal) || value is "<maximum depth reached>" or "<reference loop>");

    private enum FillerValueKind { Object, Array, String, Number, Boolean, Null }
    private sealed record FillerShape(FillerValueKind Kind,
        IReadOnlyDictionary<string, FillerShape>? Fields = null, string[]? Required = null,
        FillerShape? Element = null, bool EmptyOnly = false, bool Nullable = false,
        string? ExactString = null, bool? ExactBoolean = null, bool IntegerOnly = false);

    private static readonly FillerShape TextShape = new(FillerValueKind.String);
    private static readonly FillerShape NumberShape = new(FillerValueKind.Number);
    private static readonly FillerShape BooleanShape = new(FillerValueKind.Boolean);
    private static readonly FillerShape EmptyArrayShape = new(FillerValueKind.Array, EmptyOnly: true);
    private static FillerShape ExactText(string text) => new(FillerValueKind.String, ExactString: text);
    private static FillerShape ObjectShape(Dictionary<string, FillerShape> fields, params string[] required) =>
        new(FillerValueKind.Object, fields, required);
    private static FillerShape ReferenceShape(string type, bool nullable = false) => ObjectShape(new(StringComparer.Ordinal)
    {
        ["type"] = ExactText(type), ["name"] = TextShape,
        ["instanceId"] = new(FillerValueKind.Number, IntegerOnly: true)
    }, "type", "name", "instanceId") with { Nullable = nullable };
    private static readonly FillerShape EventShape = ObjectShape(new(StringComparer.Ordinal)
    {
        ["$type"] = ExactText("UnityEngine.Events.UnityEvent"),
        ["m_PersistentCalls"] = ObjectShape(new(StringComparer.Ordinal)
        {
            ["$type"] = ExactText("UnityEngine.Events.PersistentCallGroup"), ["m_Calls"] = EmptyArrayShape
        }, "m_Calls")
    }, "m_PersistentCalls");
    private static readonly FillerShape CurveShape = ObjectShape(new(StringComparer.Ordinal)
    {
        ["preWrapMode"] = TextShape, ["postWrapMode"] = TextShape,
        ["keys"] = new(FillerValueKind.Array, Element: ObjectShape(new(StringComparer.Ordinal)
        {
            ["time"] = NumberShape, ["value"] = NumberShape, ["inTangent"] = NumberShape, ["outTangent"] = NumberShape,
            ["inWeight"] = NumberShape, ["outWeight"] = NumberShape, ["weightedMode"] = TextShape
        }, "time", "value", "inTangent", "outTangent", "inWeight", "outWeight", "weightedMode"))
    }, "preWrapMode", "postWrapMode", "keys");

    private static readonly IReadOnlyDictionary<string, FillerShape> FillerFields = new Dictionary<string, FillerShape>(StringComparer.Ordinal)
    {
        ["$type"] = TextShape, ["OnAnimStart"] = EventShape, ["OnAnimEnd"] = EventShape, ["OnCut"] = EventShape,
        ["disableColliders"] = EmptyArrayShape, ["enableColliders"] = EmptyArrayShape, ["enableGameObjects"] = EmptyArrayShape,
        ["persistent"] = new(FillerValueKind.Null), ["callOnChildren"] = new(FillerValueKind.Boolean, ExactBoolean: false),
        ["disable"] = new(FillerValueKind.Array, Element: ReferenceShape("UnityEngine.SpriteRenderer")),
        ["enable"] = new(FillerValueKind.Array, Element: ReferenceShape("UnityEngine.SpriteRenderer")),
        ["frames"] = new(FillerValueKind.Array, Element: ReferenceShape("UnityEngine.Sprite", true)),
        ["preFrames"] = new(FillerValueKind.Array, Element: ReferenceShape("UnityEngine.Sprite", true)),
        ["pushSounds"] = new(FillerValueKind.Array, Element: ReferenceShape("UnityEngine.AudioClip")),
        ["cutSounds"] = new(FillerValueKind.Array, Element: ReferenceShape("UnityEngine.AudioClip")),
        ["particles"] = ReferenceShape("UnityEngine.GameObject", true), ["cutEffectPrefab"] = ReferenceShape("UnityEngine.GameObject", true),
        ["fps"] = NumberShape, ["walkReactAmount"] = NumberShape, ["walkReactLength"] = NumberShape,
        ["attackReactAmount"] = NumberShape, ["attackReactLength"] = NumberShape,
        ["startRandom"] = BooleanShape, ["looping"] = BooleanShape, ["playOnEnable"] = BooleanShape,
        ["visibilityCulling"] = BooleanShape, ["disableSizeMultiplier"] = BooleanShape, ["pushAnim"] = TextShape,
        ["startDelay"] = ObjectShape(new(StringComparer.Ordinal)
        { ["$type"] = ExactText("TeamCherry.SharedUtils.MinMaxFloat"), ["Start"] = NumberShape, ["End"] = NumberShape }, "Start", "End"),
        ["walkReactCurve"] = CurveShape, ["attackReactCurve"] = CurveShape
    };
}
