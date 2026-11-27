using System.Collections.Immutable;
using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

// One preparation pass. Truth nodes, occurrence ownership and display recipes are
// deliberately separate: no Rule/packed descriptor or caption-based interpretation.
internal sealed class AuditApplicationBinder(PreparedAuditRoom room, CancellationToken token)
{
    private readonly AuditPrerequisiteBuilder arena = new(token);
    private readonly Dictionary<string, AuditControl> controls = new(StringComparer.Ordinal);
    private readonly List<AuditControl> orderedControls = [];
    private AuditPrerequisiteEvidence? nonShamanCrests;
    private readonly Dictionary<AuditPrerequisiteHandle, ImmutableArray<string>> issuesByRoot = [];

    internal PreparedAuditBindings Prepare()
    {
        foreach (var movement in RoomAuditEvaluator.Movement)
        {
            token.ThrowIfCancellationRequested();
            if (movement.Directions.Length == 0) Add($"move:{movement.Key}", movement.Label, "Movement");
            foreach (var direction in movement.Directions) Add($"move:{movement.Key}:{direction}", $"{movement.Label} {direction}", "Movement");
        }
        foreach (var crest in room.SourceSnapshot.Crests)
        {
            token.ThrowIfCancellationRequested();
            Add(Key("crest", crest), crest.Name, "Crests"); Add(Key("crest-level", crest), crest.Name, "Crests", true);
        }
        // Capture the admitted original arguments record for the permanent manual
        // serializer. It is NOT regenerated from application keys or source text.
        var atoms = new AuditAtom?[room.Source.Applications.Length];
        foreach (var occurrence in room.Source.Occurrences) atoms[occurrence.Application.Index] ??= occurrence.Source;
        var applications = ImmutableArray.CreateBuilder<AuditBoundApplication>(atoms.Length);
        foreach (var application in room.Source.Applications)
        {
            token.ThrowIfCancellationRequested();
            applications.Add(Bind(application, atoms[application.Handle.Index]!));
        }
        token.ThrowIfCancellationRequested();
        return new(arena.Freeze(), applications.ToImmutable(), orderedControls.ToImmutableArray());
    }

    private void Add(string key, string label, string group, bool difficulty = false, Guid? local = null, bool requiresAdvanced = false)
    {
        if (controls.ContainsKey(key)) return;
        var control = new AuditControl(key, label, group, difficulty, local, requiresAdvanced);
        controls.Add(key, control); orderedControls.Add(control);
    }
    private static string Key(string prefix, AuditValue value) => RoomAuditEvaluator.ValueKey(prefix, value);
    private static string FixedName(string value) => RoomAuditEvaluator.Friendly(value);
    private static string MoveName(string movement) => RoomAuditEvaluator.Movement.First(x => x.Key == movement).Label;
    private static AuditNameRef Name(string role, AuditValue value) => new(role switch
    {
        "crest" => AuditNameRole.Crest, "method" => AuditNameRole.Method, "item" => AuditNameRole.Item,
        "soul" => AuditNameRole.Soul, "countable" => AuditNameRole.Countable,
        _ => throw new InvalidOperationException("Unknown audit name role")
    }, value.Id, value.Name);
    private AuditPrerequisiteEvidence Constant(bool pass, string label) => new(arena.Add(
        pass ? AuditPrerequisiteOperator.True : AuditPrerequisiteOperator.False, reason: pass ? null : label), label, []);
    private AuditPrerequisiteEvidence Block(string reason) => new(arena.Add(AuditPrerequisiteOperator.Unresolved, reason: reason), reason, []);
    private AuditPrerequisiteEvidence Condition(AuditConditionKind kind, string key, string label, int value,
        bool manual = false, Guid? local = null, AuditNameRef? name = null) => new(
            arena.Add(AuditPrerequisiteOperator.Condition, new(new(kind, key), value)), label, [], key, manual, local, name, label);
    private AuditPrerequisiteEvidence Fact(string key, string label, bool manual = false, Guid? local = null, AuditNameRef? name = null) =>
        Condition(AuditConditionKind.Fact, key, label, 1, manual, local, name);
    private AuditPrerequisiteEvidence InputFact(string key, string label, string group, bool manual = false,
        Guid? local = null, AuditNameRef? name = null, bool requiresAdvanced = false)
    { Add(key, label, group, local: local, requiresAdvanced: requiresAdvanced); return Fact(key, label, manual, local, name); }
    private AuditPrerequisiteEvidence All(params AuditPrerequisiteEvidence[] children) => Compose(false, children);
    private AuditPrerequisiteEvidence Any(params AuditPrerequisiteEvidence[] children) => Compose(true, children);
    private AuditPrerequisiteEvidence Compose(bool any, IEnumerable<AuditPrerequisiteEvidence> children)
    {
        var parts = children.ToImmutableArray();
        return new(arena.Add(any ? AuditPrerequisiteOperator.Any : AuditPrerequisiteOperator.All,
            children: parts.Select(x => x.Root).ToImmutableArray()), any ? "Any alternative" : "All prerequisites", parts);
    }
    internal static int Rank(string? difficulty) => difficulty switch
    { null or "none" or "intended" => 0, "easy" => 1, "medium" => 2, "hard" => 3, "cbt" => 4, _ => throw new InvalidOperationException("Unknown recognized audit difficulty") };
    private AuditPrerequisiteEvidence Threshold(string key, string label, string? difficulty, AuditNameRef? name = null) =>
        Condition(AuditConditionKind.Level, key, $"{label} difficulty ≥ {FixedName(difficulty ?? "none")}", Rank(difficulty), name: name) with { ConditionLabel = label };
    private AuditPrerequisiteEvidence Difficulty(string key, string? authored)
    {
        var label = FixedName(key); Add($"level:{key}", label, "Technique difficulty", true);
        return Threshold($"level:{key}", label, authored);
    }
    private AuditValue? Operand(AuditApplicationKey key, string role)
    {
        var operand = key.Operands.FirstOrDefault(x => x.Role == role);
        return operand.Id is { } id && room.OperandByRoleAndGuid.TryGetValue((role, id), out var value) && value.OutputValue == operand.Value ? value : null;
    }
    private AuditPrerequisiteEvidence Exact(string role, string output, string kind, Func<AuditValue, AuditPrerequisiteEvidence> bind)
    {
        var values = room.OperandByOutput.TryGetValue((role, output), out var bucket) ? bucket : ImmutableArray<AuditValue>.Empty;
        return values.Length == 1 ? bind(values[0]) : Block($"Required {kind} is {(values.Length == 0 ? "missing" : "ambiguous")}; correct the catalogue separately");
    }
    private AuditPrerequisiteEvidence Kit(string output) => Exact("item", output, "Item", value =>
        InputFact(Key("kit", value), value.Name, "Tools / spells / kit", name: Name("item", value)));
    private AuditPrerequisiteEvidence Crest(AuditValue crest, string? difficulty) => All(
        Fact(Key("crest", crest), "Own " + crest.Name, name: Name("crest", crest)),
        Threshold(Key("crest-level", crest), crest.Name, difficulty, Name("crest", crest)));
    private AuditPrerequisiteEvidence NamedCrest(AuditApplicationKey key)
    {
        var named = key.Operands.FirstOrDefault(x => x.Role == "crest" && x.Id is not null);
        if (named.Id is null) return key.Arguments.CrestOutputValue is null or "none" &&
            !key.Operands.Any(x => x.Role == "crest" && (x.Id is not null || x.Value != "none"))
                ? Constant(true, "No Crest specified") : Block("Named Crest no longer resolves");
        var crest = Operand(key, "crest");
        if (crest is null) return Block("Named Crest no longer resolves");
        if (room.OperandByOutput[("crest", crest.OutputValue)].Length != 1) return Block("Named Crest is ambiguous");
        return Crest(crest, key.Arguments.Difficulty) with { Label = "Own a qualifying crest" };
    }
    private AuditPrerequisiteEvidence Soul(AuditValue value)
    {
        var key = Key("soul", value); Add(key, value.Name, "Souls");
        return Any(Condition(AuditConditionKind.SoulMode, "soul-mode", "Soul Randomizer Off", 0),
            All(Condition(AuditConditionKind.SoulMode, "soul-mode", "Soul Randomizer On", 1),
                Fact(key, value.Name + " Soul", name: Name("soul", value))));
    }
    private AuditPrerequisiteEvidence TypedSoul(AuditApplicationKey key)
    {
        var value = Operand(key, "soul");
        return value is null ? Block("Soul operand no longer resolves") : Exact("soul", value.OutputValue, "Soul", Soul);
    }
    private AuditPrerequisiteEvidence Method(AuditApplicationKey key)
    {
        var value = Operand(key, "method");
        if (value is null) return Block("Method operand no longer resolves");
        if (room.OperandByOutput[("method", value.OutputValue)].Length != 1) return Block($"{value.Name} Method is ambiguous");
        if (value.OutputValue == "heal")
        {
            if (nonShamanCrests is null)
            {
                var children = ImmutableArray.CreateBuilder<AuditPrerequisiteEvidence>();
                foreach (var crest in room.SourceSnapshot.Crests)
                {
                    token.ThrowIfCancellationRequested();
                    if (crest.OutputValue != "shaman") children.Add(Fact(Key("crest", crest), "Own " + crest.Name, name: Name("crest", crest)));
                }
                nonShamanCrests = Compose(true, children) with { Label = "Any owned non-Shaman Crest" };
            }
            return All(Fact("move:heal", "Heal available"), nonShamanCrests);
        }
        return MethodKits.TryGetValue(value.OutputValue, out var output) ? Kit(output) :
            InputFact(Key("method-kit", value), value.Name, "Tools / spells / kit", name: Name("method", value));
    }
    private sealed record DirectionPart(AuditDirection Direction, AuditPrerequisiteEvidence Own, ImmutableArray<int> Path);
    private sealed record DirectionBinding(AuditPrerequisiteEvidence Root, ImmutableArray<DirectionPart> Parts,
        bool Alternative, AuditDirectionSummary Summary);
    private static AuditDirection ParseDirection(string? direction) => direction switch
    {
        null or "none" => AuditDirection.Omitted, "run" => AuditDirection.Run, "left" => AuditDirection.Left,
        "right" => AuditDirection.Right, "up" => AuditDirection.Up, "down" => AuditDirection.Down,
        "both" => AuditDirection.Both, "either" => AuditDirection.Either, "any" => AuditDirection.Any,
        "all" => AuditDirection.All, _ => throw new InvalidOperationException("Unknown recognized audit direction")
    };
    private DirectionBinding Direction(string movement, string? direction, bool freeNeedleDown = false)
    {
        var requested = ParseDirection(direction);
        AuditDirection[] supported = movement == "needle" ? new[] { AuditDirection.Left, AuditDirection.Right, AuditDirection.Up } :
            movement == "dash" ? [AuditDirection.Left, AuditDirection.Right, AuditDirection.Down] : new[] { AuditDirection.Left, AuditDirection.Right };
        AuditPrerequisiteEvidence Has(AuditDirection d) => movement == "dash" && d == AuditDirection.Down ? All(
            Fact("move:dash:run", "Swift Step Run"), Fact("move:dash:left", "Swift Step Left"), Fact("move:dash:right", "Swift Step Right")) :
            freeNeedleDown && d == AuditDirection.Down ? Constant(true, "Needle Attack Down is free") :
            Fact($"move:{movement}:{d.ToString().ToLowerInvariant()}", $"{MoveName(movement)} {d}");
        AuditDirection[] directions = requested switch
        {
            AuditDirection.Omitted or AuditDirection.All or AuditDirection.Any => supported,
            AuditDirection.Both or AuditDirection.Either => [AuditDirection.Left, AuditDirection.Right],
            _ => new[] { requested }
        };
        if (directions.Any(d => !supported.Contains(d) && !(freeNeedleDown && d == AuditDirection.Down)))
            return new(Constant(false, $"{MoveName(movement)} does not support {FixedName(direction!)}"), [], false, AuditDirectionSummary.None);
        var children = directions.Select(Has).ToArray();
        var alternative = requested is AuditDirection.Either or AuditDirection.Any;
        var aggregate = requested is AuditDirection.Omitted or AuditDirection.All or AuditDirection.Both or AuditDirection.Either or AuditDirection.Any;
        var root = aggregate ? Compose(alternative, children) : children[0];
        var parts = directions.Select((d, i) => new DirectionPart(d, children[i], aggregate ? [i] : [])).ToImmutableArray();
        var summary = movement == "dash" && (requested is AuditDirection.Omitted or AuditDirection.All or AuditDirection.Both or AuditDirection.Down) ? AuditDirectionSummary.Both :
            requested == AuditDirection.Both ? AuditDirectionSummary.Both :
            requested is AuditDirection.Omitted or AuditDirection.All ? AuditDirectionSummary.All :
            alternative ? AuditDirectionSummary.Alternatives : AuditDirectionSummary.Single;
        return new(root, parts, alternative, summary);
    }

    private AuditBoundApplication Bind(PreparedAuditApplication application, AuditAtom atom)
    {
        var key = application.Key; var a = key.Arguments; var semantic = application.Definition.Semantic;
        var predicate = new AuditNameRef(AuditNameRole.Predicate, key.PredicateId, application.Definition.Definition.Name);
        AuditPrerequisiteEvidence root;
        DirectionBinding? direction = null;
        AuditPrerequisiteEvidence? supplement = null;
        AuditNameRef? named = null;
        var form = AuditPresentationForm.Ordinary;
        var heading = ImmutableArray.Create(predicate);
        var disposition = AuditPresentationDisposition.Body;
        int? rank = null;
        switch (semantic.Kind)
        {
            case AuditBindingKind.Free: root = Constant(true, "Explicit none"); disposition = AuditPresentationDisposition.None; break;
            case AuditBindingKind.Ignored: root = Constant(true, FixedName(semantic.Key)); disposition = AuditPresentationDisposition.None; break;
            case AuditBindingKind.Impassable: root = Constant(false, "Explicit invalid / impassable"); disposition = AuditPresentationDisposition.Blocked; break;
            case AuditBindingKind.Sprint: root = Fact("move:dash:run", "Swift Step Run"); break;
            case AuditBindingKind.Directional:
                direction = Direction(semantic.Key, a.Direction); root = direction.Root; form = AuditPresentationForm.LeftRight; break;
            case AuditBindingKind.Movement: root = Fact($"move:{semantic.Key}", MoveName(semantic.Key)); break;
            case AuditBindingKind.Kit:
                root = Kit(SpellItem(semantic.Key)); if (root.Name is { } itemName) heading = [itemName]; break;
            case AuditBindingKind.Scuttlebrace:
                root = All(Kit("tool-scuttlebrace"), Direction("dash", "down").Root, Difficulty("scuttlebrace", a.Difficulty));
                form = AuditPresentationForm.Technique; rank = Rank(a.Difficulty); break;
            case AuditBindingKind.ShamanScuttler:
                root = All(Exact("crest", "shaman", "Crest", shaman => Crest(shaman, a.Difficulty)), Difficulty("scuttlebrace", a.Difficulty));
                form = AuditPresentationForm.Technique; rank = Rank(a.Difficulty); break;
            case AuditBindingKind.DrillCrystalPogo:
                root = All(Kit("tool-delver-s-drill"), Kit("tool-memory-crystal"), Difficulty("drill-crystal-pogo", a.Difficulty));
                form = AuditPresentationForm.Technique; rank = Rank(a.Difficulty); break;
            case AuditBindingKind.Technique: root = Difficulty(semantic.Key, a.Difficulty); rank = Rank(a.Difficulty); break;
            case AuditBindingKind.SoulTechnique:
                supplement = TypedSoul(key); root = All(Difficulty(semantic.Key, a.Difficulty), supplement);
                if (Operand(key, "soul") is { } actor) named = Name("soul", actor);
                rank = Rank(a.Difficulty); break;
            case AuditBindingKind.CrestPogo: case AuditBindingKind.NeedleStrike:
                root = NamedCrest(key);
                if (semantic.Kind == AuditBindingKind.NeedleStrike) root = All(root, Kit("ability-needle-strike"));
                if (Operand(key, "crest") is { } pogoCrest) { named = Name("crest", pogoCrest); form = AuditPresentationForm.Crest; rank = Rank(a.Difficulty); }
                break;
            case AuditBindingKind.Needle:
                direction = Direction("needle", a.Direction, true); root = direction.Root; form = AuditPresentationForm.Directions;
                if (key.Operands.Any(x => x.Role == "crest" && x.Id is not null))
                {
                    root = All(NamedCrest(key), root); form = AuditPresentationForm.Crest;
                    if (Operand(key, "crest") is { } needleCrest) named = Name("crest", needleCrest);
                }
                break;
            case AuditBindingKind.Stall:
                root = All(Difficulty("stall", a.Difficulty), Method(key)); form = AuditPresentationForm.Method; rank = Rank(a.Difficulty);
                if (Operand(key, "method") is { } method) named = Name("method", method); break;
            case AuditBindingKind.Soul:
                root = TypedSoul(key); form = AuditPresentationForm.Soul;
                if (Operand(key, "soul") is { } soul) named = Name("soul", soul); break;
            case AuditBindingKind.Barnak:
                supplement = Exact("soul", "Swamp Barnacle", "Soul", Soul); root = supplement;
                if (room.OperandByOutput.TryGetValue(("soul", "Swamp Barnacle"), out var barnacles) && barnacles.Length == 1) named = Name("soul", barnacles[0]); break;
            case AuditBindingKind.Item:
                var item = Operand(key, "item");
                root = item is null ? Block("Item operand no longer resolves") : KnownKitOutputs.Contains(item.OutputValue) ? Kit(item.OutputValue) :
                    InputFact(Key("item", item), "Have " + item.Name, "Other prerequisites", true, name: Name("item", item));
                if (item is not null) heading = [Name("item", item)]; break;
            case AuditBindingKind.Dependency:
                var check = key.DependencyId is { } id && room.CheckIdentityByGuid.TryGetValue(id, out var found) ? found : null;
                if (check is null) { root = Block("Check dependency no longer resolves"); break; }
                var checkRef = new AuditNameRef(AuditNameRole.Check, check.Id, check.Name, check.RoomId, check.RoomName);
                heading = [checkRef];
                root = check.RoomId == room.SourceSnapshot.RoomId ?
                    InputFact($"local-activation:{check.RoomId:D}:{check.Id:D}", room.Rows[room.RowByKey[("check", check.Id)].Index].Name,
                        "Local activations", true, check.Id, checkRef) :
                    InputFact($"external:{check.Id:D}:{check.RoomId:D}:{check.Name}", $"{check.RoomName} — {check.Name}", "External dependencies", name: checkRef);
                break;
            case AuditBindingKind.Setting:
                var settingKey = "setting:" + semantic.Key; var label = FixedName(semantic.Key); Add(settingKey, label, "Settings");
                root = Condition(AuditConditionKind.Fact, settingKey, $"{label} {(semantic.Value == 1 ? "On" : "Off")}", semantic.Value) with { ConditionLabel = label };
                disposition = AuditPresentationDisposition.HeaderOnly; break;
            case AuditBindingKind.Act:
                root = Condition(AuditConditionKind.Act, "act", semantic.Key switch
                {
                    "normal-world" => "Normal-world spawn: Act I or II", "black-thread" => "Black-thread spawn: Act III",
                    _ => "Act " + (semantic.Value == 1 ? "1" : semantic.Value == 2 ? "2" : "3")
                }, semantic.Value);
                disposition = AuditPresentationDisposition.HeaderOnly; break;
            case AuditBindingKind.Manual:
                var manualKey = "manual:" + JsonSerializer.Serialize(new { atom.PredicateId, atom.Signature, atom.Arguments,
                    Operands = atom.Operands.Select(x => new { x.Role, x.Id, x.Value }), atom.DependencyId });
                var group = key.Signature == "capability:needolin" && application.Definition.Definition.InputShape == RequirementInputSyntax.PredicateOnly
                    ? "Movement" : "Other prerequisites";
                root = InputFact(manualKey, ManualName(application, atom), group, true, name: predicate, requiresAdvanced: application.RequiresAdvanced); break;
            default: throw new InvalidOperationException("Unbound finite audit mapping");
        }
        var presentation = Presentation(application, predicate, heading, named, form, rank, root, direction, supplement, disposition);
        return new(application.Handle, root, presentation, Issues(root), root.ControlKey, root.Manual, root.LocalCheckId, ManualName(application, atom));
    }

    private AuditApplicationPresentation Presentation(PreparedAuditApplication application, AuditNameRef predicate,
        ImmutableArray<AuditNameRef> heading, AuditNameRef? named, AuditPresentationForm form, int? rank,
        AuditPrerequisiteEvidence root, DirectionBinding? direction, AuditPrerequisiteEvidence? supplement,
        AuditPresentationDisposition disposition)
    {
        if (disposition != AuditPresentationDisposition.Body) return new(disposition, [], false);
        var a = application.Key.Arguments; var kind = application.Definition.Semantic.Kind;
        var directionalBody = kind == AuditBindingKind.Directional || kind == AuditBindingKind.Needle && named is null;
        var qualifiers = new AuditPresentationQualifiers(rank is null ? null : a.Difficulty, rank,
            kind == AuditBindingKind.Sprint || directionalBody ? AuditDirection.Omitted : ParseDirection(a.Direction), a.Quantity);
        AuditPartPlan Part(AuditPrerequisiteEvidence own, ImmutableArray<int> path, AuditDirection d = AuditDirection.Omitted,
            AuditNameRef? name = null) => new(name is null ? [] : [name], d == AuditDirection.Omitted ? AuditPartRole.Identity : AuditPartRole.Direction,
                d, own, path, arena[own.Root].Condition);
        var variants = ImmutableArray.CreateBuilder<AuditVisibleVariant>();
        if (direction is not null && named is null)
        {
            // Ordinary directional OR contributes one visible refinement per child.
            // A compact technique/qualified Crest keeps intrinsic choices hidden.
            if (direction.Alternative)
                for (var i = 0; i < direction.Parts.Length; i++)
                {
                    var choice = direction.Parts[i];
                    var parts = DirectionParts(choice, Part);
                    variants.Add(new(i, [new([], i)], [new(form, heading, qualifiers, parts, true)],
                        choice.Direction == AuditDirection.Down && application.Definition.Semantic.Key == "dash" ? AuditDirectionSummary.Both : AuditDirectionSummary.Single, choice.Direction));
                }
            else
            {
                var parts = direction.Parts.Where(part => !(application.Definition.Semantic.Key == "dash" &&
                    part.Direction == AuditDirection.Down && ParseDirection(a.Direction) is AuditDirection.Omitted or AuditDirection.All))
                    .SelectMany(part => DirectionParts(part, Part)).ToImmutableArray();
                variants.Add(new(0, [], [new(form, heading, qualifiers, parts, true)], direction.Summary));
            }
        }
        else
        {
            var components = ImmutableArray.CreateBuilder<AuditPresentationComponent>();
            if (supplement is not null && named is not null)
            {
                var caller = kind == AuditBindingKind.SoulTechnique ? root.Children[0] : root;
                components.Add(new(AuditPresentationForm.Ordinary, heading, qualifiers, [Part(caller,
                    kind == AuditBindingKind.SoulTechnique ? [0] : [], name: predicate)], false));
                components.Add(new(AuditPresentationForm.Soul, [], new(null, null, AuditDirection.Omitted, null),
                    [Part(supplement, kind == AuditBindingKind.SoulTechnique ? [1] : [], name: named)], true));
            }
            else components.Add(new(form, heading, qualifiers, [Part(root, [], name: named ?? heading.FirstOrDefault())],
                form is AuditPresentationForm.Crest or AuditPresentationForm.Method or AuditPresentationForm.Soul or AuditPresentationForm.Technique));
            variants.Add(new(0, [], components.ToImmutable(), AuditDirectionSummary.None));
        }
        var eligibleDown = kind == AuditBindingKind.Directional && application.Definition.Semantic.Key == "dash" && a.Direction == "any" &&
            (application.Key.Signature is "capability:dash:{direction}" or "capability:dash:{direction}:{quantity}") &&
            application.Key.DependencyId is null && application.Key.Operands.All(x => x.Role == "direction" && x.Id is null && x.Value == "any");
        return new(disposition, variants.ToImmutable(), eligibleDown);
    }
    private static ImmutableArray<AuditPartPlan> DirectionParts(DirectionPart part,
        Func<AuditPrerequisiteEvidence, ImmutableArray<int>, AuditDirection, AuditNameRef?, AuditPartPlan> make)
    {
        if (part.Direction != AuditDirection.Down || part.Own.Children.Length != 3)
            return [make(part.Own, part.Path, part.Direction, null)];
        return [make(part.Own.Children[1], part.Path.Add(1), AuditDirection.Left, null),
            make(part.Own.Children[2], part.Path.Add(2), AuditDirection.Right, null)];
    }
    private ImmutableArray<string> Issues(AuditPrerequisiteEvidence root)
    {
        return Visit(root.Root);
        ImmutableArray<string> Visit(AuditPrerequisiteHandle handle)
        {
            token.ThrowIfCancellationRequested();
            if (issuesByRoot.TryGetValue(handle, out var cached)) return cached;
            var result = ImmutableArray.CreateBuilder<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            if (arena[handle].Reason is { } reason && seen.Add(reason)) result.Add(reason);
            foreach (var child in arena[handle].Children)
                foreach (var issue in Visit(child)) if (seen.Add(issue)) result.Add(issue);
            var answer = result.ToImmutable(); issuesByRoot.Add(handle, answer); return answer;
        }
    }
    private static string SpellItem(string name) => name switch
    {
        "silkspear" => "silk-skill-silkspear", "sharpdart" => "silk-skill-sharpdart", "thread-storm" => "silk-skill-thread-storm", "rune-rage" => "silk-skill-rune-rage",
        _ => throw new InvalidOperationException("Unknown mapped audit spell")
    };
    private static readonly ImmutableDictionary<string, string> MethodKits = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["silkspear"] = "silk-skill-silkspear", ["sharpdart"] = "silk-skill-sharpdart", ["thread-storm"] = "silk-skill-thread-storm", ["rune-rage"] = "silk-skill-rune-rage",
        ["flintslate"] = "tool-flintslate", ["voltvessels"] = "tool-voltvessels", ["flea-brew"] = "tool-flea-brew", ["pimpillo"] = "tool-pimpillo", ["longpin"] = "tool-longpin",
        ["delvers-drill"] = "tool-delver-s-drill", ["memory-crystal"] = "tool-memory-crystal", ["scuttlebrace"] = "tool-scuttlebrace", ["plasmium-phial"] = "tool-plasmium-phial",
        ["cross-stitch"] = "silk-skill-cross-stitch", ["pale-nails"] = "silk-skill-pale-nails"
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ImmutableHashSet<string> KnownKitOutputs = MethodKits.Values.Append("ability-needle-strike").ToImmutableHashSet(StringComparer.Ordinal);

    private string ManualName(PreparedAuditApplication application, AuditAtom atom)
    {
        var definition = application.Definition.Definition; var a = atom.Arguments; var predicate = definition.Name;
        string? OperandName(string role) => Operand(application.Key, role)?.Name;
        string Check()
        {
            var check = room.CheckIdentityByGuid[atom.DependencyId!.Value];
            return check.RoomId == room.SourceSnapshot.RoomId ? room.Rows[room.RowByKey[("check", check.Id)].Index].Name : $"{check.RoomName} — {check.Name}";
        }
        var difficulty = a.Difficulty is null or "none" ? null : a.Difficulty == "cbt" ? "CBT" : FixedName(a.Difficulty);
        var direction = a.Direction is null or "none" ? null : FixedName(a.Direction);
        string Quantity() => a.Quantity!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? OptionalQuantity() => a.Quantity == 1 ? null : Quantity();
        string?[] parts = definition.InputShape switch
        {
            RequirementInputSyntax.PredicateOnly => [predicate],
            RequirementInputSyntax.OptionalDifficultyThenPredicate or RequirementInputSyntax.RequiredDifficultyThenPredicate => [difficulty, predicate],
            RequirementInputSyntax.PredicateThenDirection or RequirementInputSyntax.PredicateThenOptionalDirection => [predicate, direction],
            RequirementInputSyntax.PredicateThenItem => [predicate, OperandName("item")],
            RequirementInputSyntax.PredicateThenCheck => [predicate, Check()],
            RequirementInputSyntax.PredicateThenQuantity => [predicate, Quantity()],
            RequirementInputSyntax.RequiredDifficultyThenPredicateThenSoul => [difficulty, predicate, OperandName("soul")],
            RequirementInputSyntax.PredicateThenQuantityThenCountable => [predicate, Quantity(), OperandName("countable")],
            RequirementInputSyntax.PredicateThenCountableThenQuantity => [predicate, OperandName("countable"), Quantity()],
            RequirementInputSyntax.CrestThenPredicateThenDirection or RequirementInputSyntax.OptionalCrestThenPredicateThenDirection => [OperandName("crest"), predicate, direction],
            RequirementInputSyntax.RequiredDifficultyThenCrestThenPredicate or RequirementInputSyntax.RequiredDifficultyThenOptionalCrestThenPredicate => [difficulty, OperandName("crest"), predicate],
            RequirementInputSyntax.PredicateThenSoul => [predicate, OperandName("soul")],
            RequirementInputSyntax.RequiredDifficultyThenMethodThenPredicate => [difficulty, OperandName("method"), predicate],
            RequirementInputSyntax.PredicateThenOptionalQuantity => [predicate, OptionalQuantity()],
            RequirementInputSyntax.PredicateThenDirectionThenOptionalQuantity or RequirementInputSyntax.PredicateThenOptionalDirectionThenOptionalQuantity => [predicate, direction, OptionalQuantity()],
            RequirementInputSyntax.RequiredDifficultyThenPredicateThenOptionalQuantity => [difficulty, predicate, OptionalQuantity()],
            RequirementInputSyntax.RequiredDifficultyThenPredicateThenQuantity => [difficulty, predicate, Quantity()],
            RequirementInputSyntax.RequiredDifficultyThenMethodThenPredicateThenOptionalQuantity => [difficulty, OperandName("method"), predicate, OptionalQuantity()],
            RequirementInputSyntax.RequiredDifficultyThenCrestThenPredicateThenOptionalQuantity => [difficulty, OperandName("crest"), predicate, OptionalQuantity()],
            RequirementInputSyntax.PredicateThenMethod => [predicate, OperandName("method")],
            RequirementInputSyntax.RequiredDifficultyThenPredicateThenDirection => [difficulty, predicate, direction],
            RequirementInputSyntax.RequiredDifficultyThenPredicateThenSoulThenOptionalQuantity => [difficulty, predicate, OperandName("soul"), OptionalQuantity()],
            _ => throw new InvalidOperationException("Recognized audit application has no input shape")
        };
        return string.Join(" ", parts.Where(x => !string.IsNullOrEmpty(x)));
    }
}
