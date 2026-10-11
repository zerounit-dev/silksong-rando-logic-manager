using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class SceneDumpParser
{
    public const long MaximumBytes = 256L * 1024 * 1024;

    private readonly List<SceneDumpWarning> warnings = [];
    private SceneJsonReader json = null!;

    public Task<SceneDumpReview> ParseAsync(Stream stream, CancellationToken cancellationToken = default) =>
        Task.Run(() => Parse(new AsyncReadStream(stream)), cancellationToken);

    public SceneDumpReview Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > MaximumBytes)
        {
            throw new SceneDumpTooLargeException(MaximumBytes);
        }

        warnings.Clear();

        using var limited = new ByteLimitedStream(stream, MaximumBytes);
        using var text = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        json = new SceneJsonReader(text);

        json.Expect('{');
        string sceneName = string.Empty;
        string? dumpStage = null;
        SceneMapContextEvidence? mapContext = null;
        var roots = new List<SceneDumpObject>();
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "name":
                    sceneName = json.ReadNullableString() ?? string.Empty;
                    break;
                case "dumpStage":
                    dumpStage = json.ReadNullableString();
                    break;
                case "rootObjects":
                    roots = ParseObjects("rootObjects", out _);
                    break;
                case "mapContext":
                    mapContext = ParseMapContext();
                    break;
                default:
                    json.SkipValue();
                    break;
            }

            json.TryConsume(',');
        }

        json.EnsureEnd();
        for (var index = 0; index < roots.Count; index++)
        {
            AssignContext(roots[index], null, 0, index.ToString(CultureInfo.InvariantCulture));
        }

        AssignEnemyIdentityPaths(roots, null);
        var review = new SceneDumpReview(sceneName, roots, warnings.ToArray(), VerifiedSceneUnitSize(sceneName, dumpStage, mapContext));
        ScenePlacementWorldResolver.Apply(review);
        SceneEnemySpawnNotesResolver.Apply(review);
        return review;
    }

    private SceneMapContextEvidence? ParseMapContext()
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return null;
        }

        var evidence = new SceneMapContextEvidence();
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "metadata": ParseMapMetadata(evidence); break;
                case "gameMapCurrentSceneSize": ParseMapCurrentSceneSize(evidence); break;
                case "gameMapCurrentSceneSizeValid": evidence.CurrentSizeValid = json.ReadNullableBoolean(); break;
                case "mapMatchStatus": evidence.MapMatchStatus = json.ReadNullableString(); break;
                case "selectedMapSceneName": evidence.SelectedMapSceneName = json.ReadNullableString(); break;
                case "selectedMapScene": ParseSelectedMapScene(evidence); break;
                default: json.SkipValue(); break;
            }

            json.TryConsume(',');
        }

        return evidence;
    }

    private void ParseMapMetadata(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "activeSceneName") evidence.ActiveSceneName = json.ReadNullableString();
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParseMapCurrentSceneSize(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "x") evidence.CurrentWidth = json.ReadNullableDouble();
            else if (property == "y") evidence.CurrentHeight = json.ReadNullableDouble();
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParseSelectedMapScene(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "sceneSize") ParseSelectedSceneSize(evidence);
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParseSelectedSceneSize(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "preferred") ParsePreferredSceneSize(evidence);
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParsePreferredSceneSize(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "sceneName": evidence.PreferredSceneName = json.ReadNullableString(); break;
                case "width": evidence.PreferredWidth = json.ReadNullableDouble(); break;
                case "height": evidence.PreferredHeight = json.ReadNullableDouble(); break;
                case "authoritativeForGameplayConversion": evidence.PreferredAuthoritative = json.ReadNullableBoolean(); break;
                default: json.SkipValue(); break;
            }

            json.TryConsume(',');
        }
    }

    private static SceneUnitSize? VerifiedSceneUnitSize(string sceneName, string? dumpStage, SceneMapContextEvidence? evidence)
    {
        if (evidence is not { CurrentWidth: { } width, CurrentHeight: { } height, PreferredWidth: { } preferredWidth, PreferredHeight: { } preferredHeight } ||
            !string.Equals(dumpStage, "sceneReady", StringComparison.Ordinal) ||
            !string.Equals(sceneName, evidence.ActiveSceneName, StringComparison.Ordinal) ||
            !string.Equals(sceneName, evidence.SelectedMapSceneName, StringComparison.Ordinal) ||
            !string.Equals(sceneName, evidence.PreferredSceneName, StringComparison.Ordinal) ||
            evidence.CurrentSizeValid is not true ||
            !string.Equals(evidence.MapMatchStatus, "unambiguous", StringComparison.Ordinal) ||
            evidence.PreferredAuthoritative is not true ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            width != preferredWidth || height != preferredHeight)
        {
            return null;
        }

        return new SceneUnitSize(width, height);
    }

    private List<SceneDumpObject> ParseObjects(string context, out bool reliable)
    {
        reliable = true;
        var objects = new List<SceneDumpObject>();
        if (!json.TryConsume('['))
        {
            Warn(context, "Expected an array.");
            reliable = false;
            json.SkipValue();
            return objects;
        }

        var index = 0;
        while (!json.TryConsume(']'))
        {
            if (json.Peek() != '{')
            {
                Warn($"{context}[{index}]", "Expected an object.");
                reliable = false;
                json.SkipValue();
            }
            else
            {
                objects.Add(ParseObject($"{context}[{index}]"));
            }

            index++;
            json.TryConsume(',');
        }

        return objects;
    }

    private SceneDumpObject ParseObject(string context)
    {
        json.Expect('{');
        var name = string.Empty;
        string? tag = null;
        int? layer = null;
        bool? activeSelf = null;
        bool? activeInHierarchy = null;
        ScenePosition? worldPosition = null;
        ScenePosition? localPosition = null;
        var components = new List<SceneDumpComponent>();
        var children = new List<SceneDumpObject>();
        bool componentsReliable = false, childrenReliable = false, exportError = false;
        int? instanceId = null;
        var members = new HashSet<string>(StringComparer.Ordinal);

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (!members.Add(property)) exportError = true;
            switch (property)
            {
                case "instanceId":
                    instanceId = json.ReadNullableInt();
                    break;
                case "name":
                    name = json.ReadNullableString() ?? string.Empty;
                    break;
                case "tag":
                    tag = json.ReadNullableString();
                    break;
                case "layer":
                    layer = json.ReadNullableInt();
                    break;
                case "activeSelf":
                    activeSelf = json.ReadNullableBoolean();
                    break;
                case "activeInHierarchy":
                    activeInHierarchy = json.ReadNullableBoolean();
                    break;
                case "transform":
                    (worldPosition, localPosition) = ParseTransform($"{context}.transform");
                    break;
                case "components":
                    components = ParseComponents($"{context}.components", out componentsReliable);
                    break;
                case "children":
                    children = ParseObjects($"{context}.children", out childrenReliable);
                    break;
                case "error":
                    exportError = true;
                    json.SkipValue();
                    break;
                default:
                    json.SkipValue();
                    break;
            }

            json.TryConsume(',');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Warn(context, "Object name is missing.");
        }

        if (worldPosition is null && localPosition is null)
        {
            Warn(context, "Transform position data is missing.");
        }

        return new SceneDumpObject(name, tag, layer, activeSelf, activeInHierarchy, worldPosition, localPosition, components, children)
        { HasReliableFillerEvidence = componentsReliable && childrenReliable && !exportError,
          CaptureInstanceId = instanceId, HasReliableWorldIdentity = !exportError && !string.IsNullOrWhiteSpace(name),
          HasReliableWorldComponents = componentsReliable && !exportError,
          HasReliableActivity = !exportError };
    }

    private (ScenePosition? World, ScenePosition? Local) ParseTransform(string context)
    {
        if (!json.TryConsume('{'))
        {
            Warn(context, "Expected an object.");
            json.SkipValue();
            return (null, null);
        }

        ScenePosition? world = null;
        ScenePosition? local = null;
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "position")
            {
                world = ParsePosition($"{context}.position");
            }
            else if (property == "localPosition")
            {
                local = ParsePosition($"{context}.localPosition");
            }
            else
            {
                json.SkipValue();
            }

            json.TryConsume(',');
        }

        return (world, local);
    }

    private ScenePosition? ParsePosition(string context)
    {
        if (!json.TryConsume('{'))
        {
            Warn(context, "Expected an object.");
            json.SkipValue();
            return null;
        }

        double? x = null;
        double? y = null;
        double? z = null;
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "x": x = json.ReadNullableDouble(); break;
                case "y": y = json.ReadNullableDouble(); break;
                case "z": z = json.ReadNullableDouble(); break;
                default: json.SkipValue(); break;
            }

            json.TryConsume(',');
        }

        if (x is null && y is null && z is null)
        {
            Warn(context, "Coordinate values are missing.");
        }

        return new ScenePosition(x, y, z);
    }

    private List<SceneDumpComponent> ParseComponents(string context, out bool reliable)
    {
        reliable = true;
        var components = new List<SceneDumpComponent>();
        if (!json.TryConsume('['))
        {
            Warn(context, "Expected an array.");
            reliable = false;
            json.SkipValue();
            return components;
        }

        var index = 0;
        while (!json.TryConsume(']'))
        {
            if (json.Peek() != '{')
            {
                Warn($"{context}[{index}]", "Expected an object.");
                reliable = false;
                json.SkipValue();
            }
            else
            {
                components.Add(ParseComponent($"{context}[{index}]"));
            }

            index++;
            json.TryConsume(',');
        }

        return components;
    }

    private SceneDumpComponent ParseComponent(string context)
    {
        json.Expect('{');
        var type = string.Empty;
        var evidence = new SceneComponentEvidence();
        var members = new HashSet<string>(StringComparer.Ordinal);
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (!members.Add(property)) { evidence.Filler.Invalid = true; evidence.NotesRootInvalid = true; evidence.ConditionHeaderInvalid = true; }
            if (property == "type")
            {
                type = json.ReadNullableString() ?? string.Empty;
            }
            else if (property == "data")
            {
                ScanComponentData(evidence, false, true, $"{context}.data");
            }
            else
            {
                if (property == "error") { evidence.Filler.Invalid = true; evidence.NotesRootInvalid = true; evidence.ConditionHeaderInvalid = true; }
                json.SkipValue();
            }

            json.TryConsume(',');
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            Warn(context, "Component type is missing.");
        }

        evidence.Filler.Invalid |= evidence.World.HasExportError;
        return new SceneDumpComponent(
            type,
            type.StartsWith("Persistent", StringComparison.Ordinal) ? evidence.ItemDataId : null,
            evidence.TargetScene,
            evidence.EntryPoint,
            type == "HealthManager" && evidence.IsPartOfSendToTarget is true && evidence.DamageTargetType == "HealthManager",
            type is "EnemyDeathEffectsRegular" or "EnemyDeathEffectsNoEffect" ? evidence.DirectJournal.Names.ToArray()
                : type == "PlayMakerFSM" ? evidence.FsmJournal.Names.ToArray() : [],
            type is "EnemyDeathEffectsRegular" or "EnemyDeathEffectsNoEffect" ? evidence.DirectJournal.Malformed
                : type == "PlayMakerFSM" && evidence.FsmJournal.Malformed)
        { HasReliableFillerEvidence = evidence.Filler.DataPresent && !evidence.Filler.Invalid,
          ConditionalFillerEligible = evidence.Filler.Eligible(type),
          WorldEvidence = FinishWorldEvidence(type, evidence),
          EnemyConditionEvidence = FinishEnemyConditionEvidence(type, evidence),
          HasReliableConditionCoverage = !evidence.ConditionHeaderInvalid && !string.IsNullOrWhiteSpace(type) && !IsMissingEvidence(type) };
    }

    private bool ScanComponentData(SceneComponentEvidence evidence, bool insideItemData, bool isComponentDataRoot, string context,
        FillerShape? shape = null, bool isDataType = false)
    {
        if (json.TryConsume('{'))
        {
            if (isComponentDataRoot) { evidence.Filler.DataPresent = true; evidence.Filler.ObjectData = true; }
            var valid = shape is null || shape.Kind == FillerValueKind.Object;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!json.TryConsume('}'))
            {
                var property = json.ReadPropertyName();
                json.Expect(':');
                if (!seen.Add(property)) { valid = false; evidence.Filler.Invalid = true; if (isComponentDataRoot) evidence.NotesRootInvalid = true; }
                if (property == "error") { evidence.Filler.Invalid = true; if (isComponentDataRoot) evidence.NotesRootInvalid = true; }
                FillerShape? childShape = null;
                if (isComponentDataRoot)
                {
                    evidence.Filler.Fields.Add(property);
                    FillerFields.TryGetValue(property, out childShape);
                }
                else if (shape is not null && (shape.Fields is null || !shape.Fields.TryGetValue(property, out childShape))) valid = false;
                if (isComponentDataRoot && property == "journalRecord")
                {
                    if (json.Peek() == 'n') json.SkipValue();
                    else AddJournal(evidence.DirectJournal, ParseReference());
                }
                else if (isComponentDataRoot && property == "isPartOfSendToTarget")
                {
                    evidence.IsPartOfSendToTarget = json.ReadNullableBoolean();
                }
                else if (isComponentDataRoot && property == "sendDamageTo")
                {
                    evidence.DamageTargetType = ParseReference().Type;
                }
                else if (isComponentDataRoot && property == "fsm")
                {
                    ParseFsm(evidence.FsmJournal, evidence.World);
                }
                else if (isComponentDataRoot && ReadWorldField(property, evidence))
                {
                }
                else if (property == "targetScene")
                {
                    evidence.TargetScene ??= json.ReadNullableString();
                }
                else if (property == "entryPoint")
                {
                    evidence.EntryPoint ??= json.ReadNullableString();
                }
                else if (insideItemData && property == "ID")
                {
                    evidence.ItemDataId ??= json.ReadNullableString();
                }
                else
                {
                    var childValid = ScanComponentData(evidence, insideItemData || property == "itemData", false, $"{context}.{property}",
                        childShape, isComponentDataRoot && property == "$type");
                    valid &= childValid;
                    // A supplied root type wrapper must be well-formed even for native data.
                    // Conditional field guards remain admission-only for their owning types.
                    if (isComponentDataRoot && property == "$type" && !childValid) { evidence.Filler.Invalid = true; evidence.NotesRootInvalid = true; }
                    if (isComponentDataRoot && childShape is not null && childValid) evidence.Filler.Valid.Add(property);
                }

                json.TryConsume(',');
            }

            return valid && (shape?.Required is null || shape.Required.All(seen.Contains));
        }

        if (json.TryConsume('['))
        {
            if (isComponentDataRoot) evidence.Filler.DataPresent = true;
            var valid = shape is null || shape.Kind == FillerValueKind.Array;
            var count = 0;
            while (!json.TryConsume(']'))
            {
                count++;
                valid &= ScanComponentData(evidence, insideItemData, false, context, shape?.Element);
                json.TryConsume(',');
            }

            return valid && (shape?.EmptyOnly is not true || count == 0);
        }

        if (isComponentDataRoot && json.Peek() != 'n')
        {
            Warn(context, "Component data is not an object, array, or null.");
        }

        if (json.Peek() == '"')
        {
            var stringValue = json.ReadNullableString();
            if (IsMissingEvidence(stringValue)) evidence.Filler.Invalid = true;
            if (isDataType) evidence.Filler.DataType = stringValue;
            return !isComponentDataRoot && (shape is null || shape.Kind == FillerValueKind.String &&
                !IsMissingEvidence(stringValue) && (shape.ExactString is null || shape.ExactString == stringValue));
        }
        if (isComponentDataRoot) evidence.Filler.Invalid = true;
        if (shape?.Kind == FillerValueKind.Number)
            return json.ReadNullableDouble() is { } number && double.IsFinite(number) &&
                (!shape.IntegerOnly || number is >= int.MinValue and <= int.MaxValue && Math.Truncate(number) == number);
        if (shape?.Kind == FillerValueKind.Boolean)
            return json.ReadNullableBoolean() is { } booleanValue && (shape.ExactBoolean is null || booleanValue == shape.ExactBoolean);
        if (json.Peek() == 'n')
        {
            _ = json.ReadNullableString(); // Exact null token, not a missing critical field.
            return shape is null || shape.Kind == FillerValueKind.Null || shape.Nullable;
        }
        json.SkipValue();
        return !isComponentDataRoot && shape is null;
    }

    private void AssignContext(SceneDumpObject node, Guid? parentId, int depth, string path)
    {
        node.ParentId = parentId;
        node.Depth = depth;
        node.HierarchyPath = path;
        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = node.Children[index];
            AssignContext(child, node.Id, depth + 1, $"{path}/{child.Name}[{index}]");
        }
    }

    // EPN1 comparison metadata: all retained siblings, exact names and original source order.
    // Deliberately literal/opaque, not an escaped or executable address. Diagnostic paths stay separate.
    private static void AssignEnemyIdentityPaths(IEnumerable<SceneDumpObject> siblings, string? parentPath)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in siblings)
        {
            var index = occurrences.GetValueOrDefault(node.Name);
            occurrences[node.Name] = index + 1;
            var segment = $"\"{node.Name}\"[{index.ToString(CultureInfo.InvariantCulture)}]";
            node.EnemyIdentityPath = parentPath is null ? segment : $"{parentPath}/{segment}";
            AssignEnemyIdentityPaths(node.Children, node.EnemyIdentityPath);
        }
    }

    private void Warn(string path, string message) => warnings.Add(new SceneDumpWarning(path, message));

    private sealed class SceneComponentEvidence
    {
        public string? ItemDataId { get; set; }
        public string? TargetScene { get; set; }
        public string? EntryPoint { get; set; }
        public bool? IsPartOfSendToTarget { get; set; }
        public string? DamageTargetType { get; set; }
        public SceneJournalEvidence DirectJournal { get; } = new();
        public SceneJournalEvidence FsmJournal { get; } = new();
        public FillerDataEvidence Filler { get; } = new();
        public WorldDataEvidence World { get; } = new();
        public bool NotesRootInvalid;
        public bool ConditionHeaderInvalid;
    }

    private sealed class SceneJournalEvidence
    {
        public List<string> Names { get; } = [];
        public bool Malformed { get; set; }
    }

    // Only the direct component's fsm.states[].actionData is admitted. Each state
    // retains indexed scalar arrays and reference wrappers, never its raw JSON.
    private void ParseFsm(SceneJournalEvidence evidence, WorldDataEvidence world)
    {
        var fsmFields = WorldMembers(world, property =>
        {
            if (property == "$type") { WorldType(world, "HutongGames.PlayMaker.Fsm"); return; }
            if (property != "states") { json.SkipValue(); return; }
            WorldArray(world, () =>
            {
                string? stateName = null;
                var tests = new WorldDataEvidence();
                var stateFields = WorldMembers(tests, stateProperty =>
                {
                    if (stateProperty == "$type") WorldType(tests, "HutongGames.PlayMaker.FsmState");
                    else if (stateProperty == "name") stateName = WorldText(tests);
                    else if (stateProperty == "actionData") ParseActionData(evidence, tests);
                    else json.SkipValue();
                });
                if (!stateFields.Contains("name") || !stateFields.Contains("actionData")) tests.Invalid = true;
                world.FsmUncertain |= tests.Invalid || tests.FsmUncertain;
                world.HasExportError |= tests.HasExportError;
                if (!tests.Invalid) world.FsmTests.AddRange(tests.FsmTests.Select(test => test with { State = stateName }));
            });
        });
        if (!fsmFields.Contains("states")) world.Invalid = true;
    }

    private void ParseActionData(SceneJournalEvidence evidence, WorldDataEvidence world)
    {
        List<string?> names = [], parameterNames = [], parameterTypes = [];
        List<bool?> enabled = [];
        List<int?> starts = [], positions = [];
        List<SceneReference> objects = [];
        List<SceneLiteralString> strings = [];
        List<string?> events = [];
        var actionFields = WorldMembers(world, property =>
        {
            switch (property)
            {
                case "$type": WorldType(world, "HutongGames.PlayMaker.ActionData"); break;
                case "actionNames": names = WorldValues(world, () => WorldText(world)); break;
                case "actionEnabled": enabled = WorldValues(world, () => WorldBoolean(world)); break;
                case "actionStartIndex": starts = WorldValues(world, () => WorldIndex(world)); break;
                case "paramName": parameterNames = WorldValues(world, () => WorldText(world, allowEmpty: true)); break;
                case "paramDataType": parameterTypes = WorldValues(world, () => WorldText(world)); break;
                case "paramDataPos": positions = WorldValues(world, () => WorldIndex(world)); break;
                case "fsmObjectParams": objects = ParseValues(ParseFsmObjectReference); break;
                case "fsmStringParams": strings = WorldValues(world, () => ParseLiteralString(world)); break;
                case "stringParams": events = WorldValues(world, () => WorldText(world, allowEmpty: true)); break;
                default: json.SkipValue(); break;
            }
        });
        if (!new[] { "actionNames", "actionEnabled", "actionStartIndex", "paramName", "paramDataType", "paramDataPos", "fsmStringParams", "stringParams" }.All(actionFields.Contains))
            world.Invalid = true;
        DecodeWorldTests(world, names, enabled, starts, parameterNames, parameterTypes, positions, strings, events);
        for (var action = 0; action < names.Count; action++)
        {
            if (names[action] is not ("RecordJournalKill" or "RecordJournalKillV2")) continue;
            if (action < enabled.Count && enabled[action] is false) continue;
            var start = action < starts.Count ? starts[action] : null;
            var end = action + 1 < names.Count
                ? action + 1 < starts.Count ? starts[action + 1] : null
                : parameterNames.Count;
            if (action >= enabled.Count || enabled[action] is not true ||
                start is not { } first || end is not { } last || first < 0 || last <= first ||
                last > parameterNames.Count || last > parameterTypes.Count || last > positions.Count)
            {
                evidence.Malformed = true;
                continue;
            }
            var records = 0;
            for (var parameter = first; parameter < last; parameter++)
            {
                if (parameterNames[parameter] != "Record") continue;
                records++;
                if (parameterTypes[parameter] == "FsmObject" && positions[parameter] is { } index && index >= 0 && index < objects.Count)
                    AddJournal(evidence, objects[index]);
                else evidence.Malformed = true;
            }
            if (records != 1) evidence.Malformed = true;
        }
    }

    private SceneReference ParseFsmObjectReference()
    {
        SceneReference reference = new(null, null);
        ParseMembers(property =>
        {
            if (property == "value") reference = ParseReference();
            else json.SkipValue();
        });
        return reference;
    }

    private SceneReference ParseReference()
    {
        string? type = null, name = null;
        ParseMembers(property =>
        {
            if (property == "type") type = json.ReadNullableString();
            else if (property == "name") name = json.ReadNullableString();
            else json.SkipValue();
        });
        return new(type, name);
    }

    private static void AddJournal(SceneJournalEvidence evidence, SceneReference reference)
    {
        if (reference.Type == "EnemyJournalRecord" && !string.IsNullOrWhiteSpace(reference.Name))
            evidence.Names.Add(reference.Name);
        else evidence.Malformed = true;
    }

    private void ParseMembers(Action<string> read)
    {
        if (!json.TryConsume('{')) { json.SkipValue(); return; }
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            read(property);
            json.TryConsume(',');
        }
    }

    private void ParseArray(Action read)
    {
        if (!json.TryConsume('[')) { json.SkipValue(); return; }
        while (!json.TryConsume(']')) { read(); json.TryConsume(','); }
    }

    private List<T> ParseValues<T>(Func<T> read)
    {
        List<T> values = [];
        ParseArray(() => values.Add(read()));
        return values;
    }

    private sealed record SceneReference(string? Type, string? Name);

    private sealed class SceneMapContextEvidence
    {
        public string? ActiveSceneName { get; set; }
        public double? CurrentWidth { get; set; }
        public double? CurrentHeight { get; set; }
        public bool? CurrentSizeValid { get; set; }
        public string? MapMatchStatus { get; set; }
        public string? SelectedMapSceneName { get; set; }
        public string? PreferredSceneName { get; set; }
        public double? PreferredWidth { get; set; }
        public double? PreferredHeight { get; set; }
        public bool? PreferredAuthoritative { get; set; }
    }
}

public sealed class SceneDumpReview(string sceneName, IReadOnlyList<SceneDumpObject> rootObjects, IReadOnlyList<SceneDumpWarning> warnings, SceneUnitSize? sceneUnitSize = null)
{
    public string SceneName { get; } = sceneName;
    public IReadOnlyList<SceneDumpObject> RootObjects { get; } = rootObjects;
    public IReadOnlyList<SceneDumpWarning> Warnings { get; } = warnings;
    public SceneUnitSize? SceneUnitSize { get; } = sceneUnitSize;
}

public sealed record SceneUnitSize(double Width, double Height);

public sealed class SceneDumpObject(
    string name,
    string? tag,
    int? layer,
    bool? activeSelf,
    bool? activeInHierarchy,
    ScenePosition? worldPosition,
    ScenePosition? localPosition,
    IReadOnlyList<SceneDumpComponent> components,
    IReadOnlyList<SceneDumpObject> children)
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid? ParentId { get; internal set; }
    public int Depth { get; internal set; }
    public string HierarchyPath { get; internal set; } = string.Empty;
    public string EnemyIdentityPath { get; internal set; } = string.Empty;
    public string Name { get; } = name;
    public string? Tag { get; } = tag;
    public int? Layer { get; } = layer;
    public bool? ActiveSelf { get; } = activeSelf;
    public bool? ActiveInHierarchy { get; } = activeInHierarchy;
    public ScenePosition? WorldPosition { get; } = worldPosition;
    public ScenePosition? LocalPosition { get; } = localPosition;
    public IReadOnlyList<SceneDumpComponent> Components { get; } = components;
    public IReadOnlyList<SceneDumpObject> Children { get; } = children;
    public List<string> CandidateReasons { get; } = [];
    public SceneDumpClassification Classification { get; set; }
    public bool IsAct3Only { get; internal set; }
    public int? CaptureInstanceId { get; internal set; }
    public bool HasReliableWorldIdentity { get; internal set; }
    public bool HasReliableWorldComponents { get; internal set; }
    public bool HasReliableActivity { get; internal set; }
    public string InactiveEnemyReason { get; internal set; } = string.Empty;
    public bool IsEnemyActorCandidate { get; internal set; }
    public ScenePlacementWorldAvailability PlacementWorld { get; internal set; }
    public IReadOnlyList<ScenePlayerDataCondition> PlayerDataConditions { get; internal set; } = [];
    public IReadOnlyList<SceneBooleanTest> SpawnConditions { get; internal set; } = [];
    public IReadOnlyList<string> WorldUncertainty { get; internal set; } = [];
    // Permanent scoped distinction: SW1 enemy/candidate proof versus SR1 non-enemy context.
    public bool ShowAct3Indication => IsEnemyActorCandidate || Classification == SceneDumpClassification.Enemy
        ? PlacementWorld == ScenePlacementWorldAvailability.BlackThreadOnly : IsAct3Only;
    public string? JournalName { get; internal set; }
    public bool HasReliableFillerEvidence { get; internal set; }
    public bool IsFiller { get; internal set; }
    public bool HasKeptDescendant { get; internal set; }
}

public sealed record SceneDumpComponent(string Type, string? ItemDataId, string? TargetScene, string? EntryPoint,
    bool RedirectedHealthReceiver, IReadOnlyList<string> JournalNames, bool MalformedJournal)
{
    public bool HasReliableFillerEvidence { get; internal init; }
    public bool ConditionalFillerEligible { get; internal init; }
    public SceneComponentWorldEvidence? WorldEvidence { get; internal init; }
    public SceneEnemyConditionEvidence? EnemyConditionEvidence { get; internal init; }
    public bool HasReliableConditionCoverage { get; internal init; }
}

public sealed record ScenePosition(double? X, double? Y, double? Z);

public sealed record SceneDumpWarning(string HierarchyPath, string Message);

public enum SceneDumpClassification
{
    Other,
    Exit,
    Check,
    Enemy
}

public sealed class SceneDumpParseException(string message) : Exception(message);

public sealed class SceneDumpTooLargeException(long maximumBytes) : Exception($"Scene dump exceeds the {maximumBytes / (1024 * 1024)} MiB limit.");

internal sealed class SceneJsonReader(TextReader reader)
{
    public int Peek()
    {
        SkipWhitespace();
        return reader.Peek();
    }

    public bool TryConsume(char expected)
    {
        if (Peek() != expected)
        {
            return false;
        }

        reader.Read();
        return true;
    }

    public void Expect(char expected)
    {
        if (!TryConsume(expected))
        {
            throw Error($"Expected '{expected}'.");
        }
    }

    public string ReadPropertyName()
    {
        if (Peek() != '"')
        {
            throw Error("Expected a property name.");
        }

        return ReadString();
    }

    public string? ReadNullableString()
    {
        if (Peek() == '"')
        {
            return ReadString();
        }

        if (TryReadLiteral("null"))
        {
            return null;
        }

        SkipValue();
        return null;
    }

    public int? ReadNullableInt()
    {
        var number = ReadNullableDouble();
        return number is >= int.MinValue and <= int.MaxValue && Math.Truncate(number.Value) == number.Value ? (int)number.Value : null;
    }

    public double? ReadNullableDouble()
    {
        var first = Peek();
        if (first == 'n')
        {
            TryReadLiteral("null");
            return null;
        }

        if (first is not ('-' or >= '0' and <= '9'))
        {
            SkipValue();
            return null;
        }

        var token = new StringBuilder();
        while (reader.Peek() is var next && next >= 0 && "-+0123456789.eE".Contains((char)next))
        {
            token.Append((char)reader.Read());
        }

        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public bool? ReadNullableBoolean()
    {
        if (TryReadLiteral("true")) return true;
        if (TryReadLiteral("false")) return false;
        if (TryReadLiteral("null")) return null;
        SkipValue();
        return null;
    }

    public void SkipValue()
    {
        switch (Peek())
        {
            case '"':
                _ = ReadString();
                return;
            case '{':
                reader.Read();
                while (!TryConsume('}'))
                {
                    _ = ReadPropertyName();
                    Expect(':');
                    SkipValue();
                    TryConsume(',');
                }
                return;
            case '[':
                reader.Read();
                while (!TryConsume(']'))
                {
                    SkipValue();
                    TryConsume(',');
                }
                return;
            case -1:
                throw Error("Unexpected end of document.");
            default:
                while (reader.Peek() is var next && next >= 0 && !char.IsWhiteSpace((char)next) && !",]}".Contains((char)next))
                {
                    reader.Read();
                }
                return;
        }
    }

    public void EnsureEnd()
    {
        if (Peek() != -1)
        {
            throw Error("Unexpected content after the scene dump.");
        }
    }

    private string ReadString()
    {
        Expect('"');
        var value = new StringBuilder();
        while (true)
        {
            var next = reader.Read();
            if (next < 0) throw Error("Unterminated string.");
            if (next == '"') return value.ToString();
            if (next != '\\')
            {
                value.Append((char)next);
                continue;
            }

            var escape = reader.Read();
            value.Append(escape switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => ReadUnicodeCharacter(),
                _ => throw Error("Invalid string escape.")
            });
        }
    }

    private char ReadUnicodeCharacter()
    {
        Span<char> hex = stackalloc char[4];
        for (var index = 0; index < hex.Length; index++)
        {
            var next = reader.Read();
            if (next < 0) throw Error("Incomplete unicode escape.");
            hex[index] = (char)next;
        }

        return ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? (char)value
            : throw Error("Invalid unicode escape.");
    }

    private bool TryReadLiteral(string literal)
    {
        if (Peek() != literal[0]) return false;
        foreach (var expected in literal)
        {
            if (reader.Read() != expected)
            {
                throw Error($"Invalid literal; expected '{literal}'.");
            }
        }

        return true;
    }

    private void SkipWhitespace()
    {
        while (reader.Peek() is var next && next >= 0 && char.IsWhiteSpace((char)next))
        {
            reader.Read();
        }
    }

    private static SceneDumpParseException Error(string message) => new(message);
}

internal sealed class ByteLimitedStream(Stream inner, long maximumBytes) : Stream
{
    private long bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => bytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (bytesRead >= maximumBytes)
        {
            if (inner.ReadByte() >= 0) throw new SceneDumpTooLargeException(maximumBytes);
            return 0;
        }

        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, maximumBytes - bytesRead)]);
        bytesRead += read;
        return read;
    }

    public override int ReadByte()
    {
        if (bytesRead >= maximumBytes)
        {
            if (inner.ReadByte() >= 0) throw new SceneDumpTooLargeException(maximumBytes);
            return -1;
        }

        var value = inner.ReadByte();
        if (value >= 0) bytesRead++;
        return value;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class AsyncReadStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override int Read(Span<byte> buffer)
    {
        var copy = new byte[buffer.Length];
        var read = inner.ReadAsync(copy).AsTask().GetAwaiter().GetResult();
        copy.AsSpan(0, read).CopyTo(buffer);
        return read;
    }
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
