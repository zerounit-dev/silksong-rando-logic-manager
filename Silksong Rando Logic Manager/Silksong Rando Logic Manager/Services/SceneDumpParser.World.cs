namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class SceneDumpParser
{
    private sealed class WorldDataEvidence
    {
        public bool Invalid;
        public bool HasExportError;
        public bool FsmUncertain;
        public string? BoolName;
        public bool? WaitForStart;
        public SceneWorldReference? Activate, Deactivate, GateTarget;
        public SceneBooleanTest? Test;
        public HashSet<string> Valid { get; } = new(StringComparer.Ordinal);
        public List<SceneFsmBooleanTest> FsmTests { get; } = [];
    }

    private bool ReadWorldField(string property, SceneComponentEvidence component)
    {
        var world = component.World;
        bool valid;
        switch (property)
        {
            case "activateGameObject": world.Activate = ParseWorldReference(world); return true;
            case "deactivateGameObject": world.Deactivate = ParseWorldReference(world); return true;
            case "objectToDeactivate":
            case "objectToActivate": world.GateTarget = ParseWorldReference(world); return true;
            case "boolName": world.BoolName = WorldText(world); return true;
            case "waitForStart": world.WaitForStart = WorldBoolean(world); valid = world.WaitForStart.HasValue; break;
            case "playerDataTest": world.Test = ParseWorldPlayerDataTest(world); return true;
            case "questTests": case "equipTests": case "entryGateWhitelist": case "entryGateBlacklist":
                valid = ReadEmptyWorldArray(world); break;
            case "checkActive": valid = ReadExplicitNull(world); break;
            case "expectedActive": valid = WorldBoolean(world).HasValue; break;
            case "activateEventRegister": case "deactivateEventRegister":
                valid = WorldText(world, allowEmpty: true) == string.Empty; break;
            default: return false;
        }
        if (valid) world.Valid.Add(property);
        else world.Invalid = true;
        return true;
    }

    private bool ReadExplicitNull(WorldDataEvidence world)
    {
        var valid = json.Peek() == 'n';
        WorldSkip(world);
        return valid;
    }

    private bool ReadEmptyWorldArray(WorldDataEvidence world)
    {
        var count = 0;
        WorldArray(world, () => { count++; WorldSkip(world); });
        return !world.Invalid && count == 0;
    }

    // Exact finite owned schemas. No retained JSON, expression tree or recursive proximity matching.
    private HashSet<string> WorldMembers(WorldDataEvidence world, Action<string> read)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!json.TryConsume('{')) { world.Invalid = true; WorldSkip(world); return seen; }
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (!seen.Add(property) || property == "error") { world.Invalid = true; world.HasExportError = true; }
            read(property);
            json.TryConsume(',');
        }
        return seen;
    }

    private void WorldArray(WorldDataEvidence world, Action read)
    {
        if (!json.TryConsume('[')) { world.Invalid = true; WorldSkip(world); return; }
        while (!json.TryConsume(']')) { read(); json.TryConsume(','); }
    }

    private List<T> WorldValues<T>(WorldDataEvidence world, Func<T> read)
    {
        List<T> values = [];
        WorldArray(world, () => values.Add(read()));
        return values;
    }

    private string? WorldText(WorldDataEvidence world, bool allowEmpty = false)
    {
        if (json.Peek() is not ('"' or 'n')) { world.Invalid = true; WorldSkip(world); return null; }
        var value = json.ReadNullableString();
        if (IsMissingEvidence(value)) world.HasExportError = true;
        if (value is null || IsMissingEvidence(value) || !allowEmpty && string.IsNullOrWhiteSpace(value)) world.Invalid = true;
        return value;
    }

    private void WorldType(WorldDataEvidence world, string expected)
    {
        if (WorldText(world) != expected) world.Invalid = true;
    }

    private bool? WorldBoolean(WorldDataEvidence world)
    {
        if (json.Peek() is not ('t' or 'f' or 'n')) { world.Invalid = true; WorldSkip(world); return null; }
        var value = json.ReadNullableBoolean();
        if (value is null) world.Invalid = true;
        return value;
    }

    private int? WorldIndex(WorldDataEvidence world)
    {
        var number = WorldNumber(world);
        var value = number is >= int.MinValue and <= int.MaxValue && Math.Truncate(number.Value) == number.Value ? (int?)number.Value : null;
        if (value is null or < 0) world.Invalid = true;
        return value;
    }

    private double? WorldNumber(WorldDataEvidence world)
    {
        if (json.Peek() is not ('-' or >= '0' and <= '9' or 'n')) { world.Invalid = true; WorldSkip(world); return null; }
        var value = json.ReadNullableDouble();
        if (value is null || !double.IsFinite(value.Value)) world.Invalid = true;
        return value;
    }

    private int? WorldInteger(WorldDataEvidence world)
    {
        var number = WorldNumber(world);
        if (number is >= int.MinValue and <= int.MaxValue && Math.Truncate(number.Value) == number.Value) return (int)number.Value;
        world.Invalid = true;
        return null;
    }

    // Preserve the existing streaming scan's error/duplicate/marker retention even
    // when an owned world field has the wrong shape. This assigns no game semantics.
    private void WorldSkip(WorldDataEvidence world)
    {
        if (json.Peek() == '{') { WorldMembers(world, _ => WorldSkip(world)); return; }
        if (json.Peek() == '[') { WorldArray(world, () => WorldSkip(world)); return; }
        if (json.Peek() == '"')
        {
            if (IsMissingEvidence(json.ReadNullableString())) { world.Invalid = true; world.HasExportError = true; }
            return;
        }
        json.SkipValue();
    }

    private SceneWorldReference ParseWorldReference(WorldDataEvidence parent)
    {
        if (json.Peek() == 'n') { json.SkipValue(); return new(null, null, null, true, true); }
        var world = new WorldDataEvidence();
        string? type = null, name = null;
        int? id = null;
        var seen = WorldMembers(world, property =>
        {
            switch (property)
            {
                case "type": type = WorldText(world); break;
                case "name": name = WorldText(world, allowEmpty: true); break;
                case "instanceId": id = WorldInteger(world); if (id is 0) world.Invalid = true; break;
                default: world.Invalid = true; WorldSkip(world); break;
            }
        });
        var valid = !world.Invalid && seen.Contains("type") && seen.Contains("instanceId") && type == "UnityEngine.GameObject";
        parent.Invalid |= !valid;
        parent.HasExportError |= world.HasExportError;
        return new(type, name, id, false, valid);
    }

    private SceneBooleanTest? ParseWorldPlayerDataTest(WorldDataEvidence parent)
    {
        var world = new WorldDataEvidence();
        List<SceneBooleanTest?> tests = [];
        var groups = 0;
        var seen = WorldMembers(world, property =>
        {
            if (property == "$type") WorldType(world, "PlayerDataTest");
            else if (property == "TestGroups") WorldArray(world, () =>
            {
                groups++;
                var groupFields = WorldMembers(world, groupProperty =>
                {
                    if (groupProperty == "$type") WorldType(world, "PlayerDataTest+TestGroup");
                    else if (groupProperty == "Tests") WorldArray(world, () => tests.Add(ParseWorldBooleanTest(world)));
                    else { world.Invalid = true; WorldSkip(world); }
                });
                if (!groupFields.Contains("Tests")) world.Invalid = true;
            });
            else { world.Invalid = true; WorldSkip(world); }
        });
        var valid = !world.Invalid && seen.Contains("TestGroups") && groups == 1 && tests.Count == 1 && tests[0] is not null;
        parent.Invalid |= !valid;
        parent.HasExportError |= world.HasExportError;
        return valid ? tests[0] : null;
    }

    private SceneBooleanTest? ParseWorldBooleanTest(WorldDataEvidence parent)
    {
        var world = new WorldDataEvidence();
        string? type = null, field = null;
        bool? value = null;
        var seen = WorldMembers(world, property =>
        {
            switch (property)
            {
                case "$type": WorldType(world, "PlayerDataTest+Test"); break;
                case "Type": type = WorldText(world); break;
                case "FieldName": field = WorldText(world); break;
                case "BoolValue": value = WorldBoolean(world); break;
                case "NumType": case "StringType": case "StringValue": _ = WorldText(world, allowEmpty: true); break;
                case "IntValue": _ = WorldInteger(world); break;
                case "FloatValue": _ = WorldNumber(world); break;
                default: world.Invalid = true; WorldSkip(world); break;
            }
        });
        var valid = !world.Invalid && type == "Bool" && field is not null && value.HasValue &&
            new[] { "Type", "FieldName", "BoolValue" }.All(seen.Contains);
        parent.Invalid |= !valid;
        parent.HasExportError |= world.HasExportError;
        return valid ? new(field!, value!.Value) : null;
    }

    private static SceneComponentWorldEvidence? FinishWorldEvidence(string type, SceneComponentEvidence component)
    {
        if (type is not ("TestGameObjectActivator" or "DeactivateIfPlayerdataFalse" or "DeactivateIfPlayerdataTrue" or
            "ActivateIfPlayerdataFalse" or "ActivateIfPlayerdataTrue" or "BlackThreadState" or "PlayMakerFSM")) return null;
        var world = component.World;
        var fields = component.Filler.Fields;
        var valid = component.Filler.DataPresent && component.Filler.ObjectData && !component.Filler.Invalid && !world.Invalid;
        if (component.Filler.DataType is { } wrapper && wrapper != type) valid = false;
        if (type == "TestGameObjectActivator")
        {
            string[] required = ["activateGameObject", "deactivateGameObject", "activateEventRegister", "deactivateEventRegister",
                "playerDataTest", "questTests", "equipTests", "entryGateWhitelist", "entryGateBlacklist", "checkActive", "expectedActive"];
            valid &= required.All(fields.Contains) && fields.All(field => field == "$type" || required.Contains(field)) &&
                required.Where(field => field is not ("activateGameObject" or "deactivateGameObject" or "playerDataTest")).All(world.Valid.Contains) &&
                world.Test is not null && world.Activate is { IsNull: false, Valid: true } && world.Deactivate is { IsNull: false, Valid: true };
        }
        else if (type is "DeactivateIfPlayerdataFalse" or "DeactivateIfPlayerdataTrue" or "ActivateIfPlayerdataFalse" or "ActivateIfPlayerdataTrue")
        {
            var target = type.StartsWith("Deactivate", StringComparison.Ordinal) ? "objectToDeactivate" : "objectToActivate";
            valid &= fields.Contains("boolName") && fields.Contains(target) && world.BoolName is not null && world.GateTarget is { Valid: true } &&
                fields.All(field => field == "$type" || field == "boolName" || field == target || field == "waitForStart") &&
                (!fields.Contains("waitForStart") || world.Valid.Contains("waitForStart"));
        }
        else if (type == "PlayMakerFSM") valid &= fields.Contains("fsm");
        return new(valid, world.Test, world.Activate, world.Deactivate, world.BoolName, world.GateTarget,
            world.WaitForStart, world.FsmTests.ToArray(), world.FsmUncertain);
    }

    private sealed record SceneLiteralString(string? Value, bool Valid);

    private SceneLiteralString ParseLiteralString(WorldDataEvidence parent)
    {
        var world = new WorldDataEvidence();
        string? value = null;
        bool? useVariable = null;
        var seen = WorldMembers(world, property =>
        {
            switch (property)
            {
                case "$type": WorldType(world, "HutongGames.PlayMaker.FsmString"); break;
                case "value": value = WorldText(world, allowEmpty: true); break;
                case "useVariable": useVariable = WorldBoolean(world); break;
                case "name": case "tooltip": _ = WorldText(world, allowEmpty: true); break;
                case "showInInspector": case "networkSync": _ = WorldBoolean(world); break;
                default: world.Invalid = true; WorldSkip(world); break;
            }
        });
        parent.FsmUncertain |= world.Invalid;
        parent.HasExportError |= world.HasExportError;
        return new(value, !world.Invalid && seen.Contains("value") && seen.Contains("useVariable") && useVariable is false);
    }

    private static void DecodeWorldTests(WorldDataEvidence world, List<string?> actions, List<bool?> enabled, List<int?> starts,
        List<string?> names, List<string?> types, List<int?> positions, List<SceneLiteralString> strings, List<string?> events)
    {
        for (var action = 0; action < actions.Count; action++)
        {
            if (actions[action] is not ("PlayerDataBoolTest" or "HutongGames.PlayMaker.Actions.PlayerDataBoolTest")) continue;
            if (action < enabled.Count && enabled[action] is false) continue;
            var first = action < starts.Count ? starts[action] : null;
            var last = action + 1 < actions.Count ? action + 1 < starts.Count ? starts[action + 1] : null : names.Count;
            if (action >= enabled.Count || enabled[action] is not true || first is not { } begin || last is not { } end ||
                begin < 0 || end <= begin || end > names.Count || names.Count != types.Count || names.Count != positions.Count ||
                starts.Count != actions.Count || enabled.Count != actions.Count ||
                starts.Any(start => start is null || start < 0 || start > names.Count) ||
                starts.Zip(starts.Skip(1)).Any(pair => pair.First > pair.Second))
            { world.FsmUncertain = true; continue; }
            string? field = null, isTrue = null, isFalse = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var valid = true;
            for (var parameter = begin; parameter < end; parameter++)
            {
                var name = names[parameter];
                if (name is not ("boolName" or "isTrue" or "isFalse")) { valid = false; continue; }
                if (!seen.Add(name)) valid = false;
                var index = positions[parameter];
                if (index is not { } pool || pool < 0) { valid = false; continue; }
                if (name == "boolName")
                {
                    if (types[parameter] == "FsmString" && pool < strings.Count && strings[pool].Valid && !string.IsNullOrWhiteSpace(strings[pool].Value))
                        field = strings[pool].Value;
                    else valid = false;
                }
                else if (types[parameter] == "FsmEvent" && pool < events.Count && events[pool] is { } eventName)
                { if (name == "isTrue") isTrue = eventName; else isFalse = eventName; }
                else valid = false;
            }
            valid &= field is not null && seen.Count == 3;
            if (valid) world.FsmTests.Add(new(field!, isTrue!, isFalse!, null, action));
            else world.FsmUncertain = true;
        }
    }
}

public sealed record SceneBooleanTest(string Field, bool Value);
public sealed record SceneWorldReference(string? Type, string? Name, int? InstanceId, bool IsNull, bool Valid);
public sealed record SceneFsmBooleanTest(string Field, string IsTrue, string IsFalse, string? State, int ActionIndex);
public sealed record SceneComponentWorldEvidence(bool Valid, SceneBooleanTest? Test, SceneWorldReference? Activate,
    SceneWorldReference? Deactivate, string? BoolName, SceneWorldReference? GateTarget, bool? WaitForStart,
    IReadOnlyList<SceneFsmBooleanTest> FsmTests, bool HasFsmUncertainty);
public enum ScenePlacementWorldAvailability { Unknown, BlackThreadOnly, NormalOnly, Act3AffectedOrBoth }
public sealed record ScenePlayerDataCondition(string Field, bool? RequiredValue, Guid SourceObjectId, string Source,
    string? IsTrue = null, string? IsFalse = null);
