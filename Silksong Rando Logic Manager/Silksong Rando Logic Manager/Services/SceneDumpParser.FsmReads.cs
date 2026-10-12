namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class SceneDumpParser
{
    // Documentary input reads only. No expected-value, event or output semantics escape.
    private bool ParseDocumentaryBooleanVariable()
    {
        var value = new WorldDataEvidence();
        string? type = null;
        bool? useVariable = null, boolValue = null;
        var fields = WorldMembers(value, property =>
        {
            switch (property)
            {
                case "$type": WorldType(value, "HutongGames.PlayMaker.FsmVar"); break;
                case "type": type = WorldText(value); break;
                case "useVariable": useVariable = WorldBoolean(value); break;
                case "boolValue": boolValue = WorldBoolean(value); break;
                case "name": case "tooltip": _ = WorldText(value, allowEmpty: true); break;
                case "showInInspector": case "networkSync": _ = WorldBoolean(value); break;
                // FsmVar serializes defaults for other scalar/array types too. In
                // particular arrayValue.type=Unknown is not the enclosing type.
                default: WorldSkip(value); break;
            }
        });
        return !value.Invalid && !value.HasExportError && type == "Bool" && useVariable is false && boolValue.HasValue &&
            new[] { "type", "useVariable", "boolValue" }.All(fields.Contains);
    }

    private static void DecodeDocumentaryReads(List<string> reads, HashSet<string> fields, List<string?> actions,
        List<bool?> enabled, List<int?> starts, List<string?> names, List<string?> types, List<int?> positions,
        List<SceneLiteralString> strings, List<bool> variables, bool variablePoolValid)
    {
        if (!new[] { "actionNames", "actionEnabled", "actionStartIndex", "paramName", "paramDataType", "paramDataPos", "fsmStringParams" }.All(fields.Contains) ||
            actions.Count != enabled.Count || actions.Count != starts.Count || names.Count != types.Count || names.Count != positions.Count ||
            starts.Any(start => start is null || start < 0 || start > names.Count) ||
            starts.Zip(starts.Skip(1)).Any(pair => pair.First > pair.Second)) return;
        for (var action = 0; action < actions.Count; action++)
        {
            var kind = actions[action];
            const string prefix = "HutongGames.PlayMaker.Actions.";
            if (kind?.StartsWith(prefix, StringComparison.Ordinal) is true) kind = kind[prefix.Length..];
            if (kind is not ("PlayerDataBoolTest" or "PlayerDataVariableTest" or "GetPlayerDataBool") || enabled[action] is not true) continue;
            var begin = starts[action]!.Value;
            var end = action + 1 < actions.Count ? starts[action + 1]!.Value : names.Count;
            if (end <= begin) continue;
            string[] signature = kind switch
            {
                "PlayerDataBoolTest" => ["boolName", "isTrue", "isFalse"],
                "PlayerDataVariableTest" => ["VariableName", "ExpectedValue", "IsExpectedEvent", "IsNotExpectedEvent"],
                _ => ["boolName", "storeValue"]
            };
            if (end - begin != signature.Length) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? name = null;
            var valid = true;
            for (var parameter = begin; parameter < end; parameter++)
            {
                var key = names[parameter];
                if (key is null || !signature.Contains(key, StringComparer.Ordinal) || !seen.Add(key)) { valid = false; continue; }
                var expectedType = key switch
                {
                    "boolName" or "VariableName" => "FsmString",
                    "ExpectedValue" => "FsmVar",
                    "storeValue" => "FsmBool",
                    _ => "FsmEvent"
                };
                if (types[parameter] != expectedType || positions[parameter] is not { } pool || pool < 0) { valid = false; continue; }
                if (expectedType == "FsmString")
                {
                    if (pool < strings.Count && strings[pool].Valid && !string.IsNullOrWhiteSpace(strings[pool].Value)) name = strings[pool].Value;
                    else valid = false;
                }
                else if (expectedType == "FsmVar" && (!fields.Contains("fsmVarParams") || !variablePoolValid || pool >= variables.Count || !variables[pool])) valid = false;
                // Event values and the getter's variable-backed output are not input reads.
            }
            if (valid && seen.Count == signature.Length && name is not null) reads.Add(name);
        }
    }
}
