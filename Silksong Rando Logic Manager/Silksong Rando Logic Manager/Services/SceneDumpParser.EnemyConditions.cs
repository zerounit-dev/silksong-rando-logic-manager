namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class SceneDumpParser
{
    // EPN3: the same forward-only owned read supplies Notes, never broader SW1 proof.
    private bool ReadQuestTests(WorldDataEvidence parent)
    {
        var array = new WorldDataEvidence();
        var count = 0;
        var arrayShape = json.Peek() == '[';
        WorldArray(array, () =>
        {
            count++;
            ReadQuestTest(array);
        });
        parent.HasExportError |= array.HasExportError;
        parent.Invalid |= array.Invalid;
        parent.NotesPresence |= array.NotesPresence;
        parent.NotesIncomplete |= !arrayShape || array.HasExportError || array.NotesIncomplete;
        parent.QuestNotesFacts.AddRange(array.NotesFacts);
        // Identical to SW1's former empty-array admission, including prior invalidity.
        return !parent.Invalid && count == 0;
    }

    private static readonly (string Selector, string Value, string Label)[] QuestChecks =
    [
        ("CheckAvailable", "IsAvailable", "available"),
        ("CheckAccepted", "IsAccepted", "accepted"),
        ("CheckCompletable", "IsCompletable", "completable"),
        ("CheckCompleted", "IsCompleted", "completed"),
        ("CheckWasEverCompleted", "WasEverCompleted", "wasEverCompleted")
    ];

    private void ReadQuestTest(WorldDataEvidence parent)
    {
        var entry = new WorldDataEvidence();
        var booleans = new Dictionary<string, bool?>(StringComparer.Ordinal);
        string? questName = null;
        var wrapperValid = true;
        var unknown = false;
        var objectShape = json.Peek() == '{';
        var seen = WorldMembers(entry, property =>
        {
            if (property == "$type") wrapperValid &= WorldText(entry) == "QuestTest";
            else if (property == "Quest") questName = ReadQuestReference(entry);
            else if (QuestChecks.Any(check => check.Selector == property || check.Value == property) || property == "CheckCompletedAmount")
                booleans[property] = WorldBoolean(entry);
            else if (property == "CompletedAmount") _ = WorldNumber(entry);
            else { unknown = true; WorldSkip(entry); }
        });
        parent.HasExportError |= entry.HasExportError;
        parent.Invalid |= entry.Invalid || !wrapperValid;
        // Selected criteria count even if their name/value cannot be printed.
        parent.NotesPresence |= booleans.Any(pair => pair.Key.StartsWith("Check", StringComparison.Ordinal) && pair.Value == true);
        var trustworthy = objectShape && wrapperValid && !entry.HasExportError && questName is not null;
        parent.NotesIncomplete |= unknown || !trustworthy || booleans.GetValueOrDefault("CheckCompletedAmount") is not false;
        foreach (var check in QuestChecks)
        {
            var selected = booleans.GetValueOrDefault(check.Selector);
            if (selected is null) parent.NotesIncomplete = true;
            if (selected is not true) continue;
            if (booleans.GetValueOrDefault(check.Value) is not { } expected) { parent.NotesIncomplete = true; continue; }
            if (trustworthy) parent.NotesFacts.Add(new($"{questName} {check.Label}", expected));
        }
        // No missing/unknown selector or malformed entry is evidence of absence.
        parent.NotesIncomplete |= !seen.Contains("Quest");
    }

    private string? ReadQuestReference(WorldDataEvidence parent)
    {
        var reference = new WorldDataEvidence();
        string? type = null, name = null;
        var seen = WorldMembers(reference, property =>
        {
            switch (property)
            {
                case "type": type = WorldText(reference); break;
                case "name": name = WorldText(reference); break;
                case "instanceId": if (WorldInteger(reference) is 0) reference.Invalid = true; break;
                default: reference.Invalid = true; WorldSkip(reference); break;
            }
        });
        parent.HasExportError |= reference.HasExportError;
        var valid = !reference.Invalid && type == "Quest" && seen.Contains("type") && seen.Contains("name");
        parent.Invalid |= !valid;
        return valid ? name : null;
    }

    private static SceneEnemyConditionEvidence? FinishEnemyConditionEvidence(string type, SceneComponentEvidence component)
    {
        if (type is not ("TestGameObjectActivator" or "DeactivateIfPlayerdataFalse" or "DeactivateIfPlayerdataTrue" or
            "ActivateIfPlayerdataFalse" or "ActivateIfPlayerdataTrue")) return null;
        var world = component.World;
        var fields = component.Filler.Fields;
        var trusted = component.Filler.DataPresent && component.Filler.ObjectData && !component.Filler.Invalid &&
            (component.Filler.DataType is null || component.Filler.DataType == type);
        var targetsTrusted = component.Filler.DataPresent && component.Filler.ObjectData && !component.NotesRootInvalid &&
            (component.Filler.DataType is null || component.Filler.DataType == type);
        if (type != "TestGameObjectActivator")
        {
            var simple = FinishWorldEvidence(type, component);
            return new(targetsTrusted, simple is { NotesValid: true }, simple is not { NotesValid: true },
                simple is { NotesValid: true, BoolName: { } field }
                    ? [new(field, type is "ActivateIfPlayerdataTrue" or "DeactivateIfPlayerdataFalse")] : []);
        }
        string[] required = ["activateGameObject", "deactivateGameObject", "activateEventRegister", "deactivateEventRegister",
            "playerDataTest", "questTests", "equipTests", "entryGateWhitelist", "entryGateBlacklist", "checkActive", "expectedActive"];
        // Strict SW1 Test remains the only allowed opposite-target inverse.
        var incomplete = !trusted || world.NotesIncomplete || !required.All(fields.Contains) ||
            fields.Any(field => field != "$type" && !required.Contains(field));
        // Bool group/test facts precede quest array/check facts regardless of JSON member order.
        return new(targetsTrusted, world.NotesPresence, incomplete,
            trusted ? world.NotesFacts.Concat(world.QuestNotesFacts).ToArray() : []);
    }
}

public sealed record SceneEnemyConditionEvidence(bool HasReliableTargets, bool HasConditions, bool Incomplete, IReadOnlyList<SceneBooleanTest> Facts);
