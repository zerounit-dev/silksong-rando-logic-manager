using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class AuditScenario
{
    public string Start { get; set; } = "";
    public int Act { get; set; } = 1;
    public bool SoulRandomizer { get; set; }
    public Dictionary<string, bool> Facts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Levels { get; set; } = new(StringComparer.Ordinal);
    public bool Has(string key) => Facts.GetValueOrDefault(key);
    public int Level(string key) => Levels.GetValueOrDefault(key);
}
public sealed record AuditControl(string Key, string Label, string Group, bool Difficulty = false);
public sealed record AuditStart(string Key, string Label, Guid? NodeId, bool Valid);
public sealed record AuditPrerequisite(bool Passed, string Label, bool Unresolved,
    bool Alternatives, IReadOnlyList<AuditPrerequisite> Children);
public sealed record AuditLeafResult(bool Passed, string Explanation, bool Unresolved = false,
    AuditPrerequisite? Evidence = null, string? ControlKey = null, bool Manual = false, Guid? LocalCheckId = null, bool WitnessUsed = false);
public sealed record AuditRowResult(bool Available, string Access, IReadOnlyDictionary<AuditAtom, AuditLeafResult> Leaves, bool GatePassed);
// Immutable first-justification DAG: every source/dependency already existed when this fact was added.
public sealed record AuditProof(Guid NodeId, AuditRow? Via, AuditProof? Source,
    IReadOnlyDictionary<AuditAtom, AuditLeafResult> Leaves, IReadOnlyDictionary<Guid, AuditProof> Dependencies, int Order);
public sealed record AuditEvaluation(IReadOnlySet<Guid> Reached, IReadOnlySet<Guid> Completed,
    IReadOnlyDictionary<(string Kind, Guid Id), AuditRowResult> Rows, bool HasOrigin,
    IReadOnlyDictionary<Guid, AuditProof> NodeProofs, IReadOnlyDictionary<Guid, AuditProof> CheckProofs);

/// <summary>Room-local scenario rules only. No inventory model, external traversal or persistence.</summary>
public sealed class RoomAuditEvaluator
{
    public static readonly string[] DifficultyNames = ["None", "Easy", "Medium", "Hard", "CBT"];
    public static readonly (string Key, string Label, string[] Directions)[] Movement =
    [
        ("needle", "Needle Attack", ["left", "right", "up"]),
        ("dash", "Swift Step", ["run", "left", "right"]),
        ("ledge-grab", "Ledge Grab", ["left", "right"]),
        ("cling-grip", "Cling Grip", ["left", "right"]),
        ("swim", "Swim", []),
        ("drifters-cloak", "Drifter's Cloak", []),
        ("faydown-cloak", "Faydown Cloak", []),
        ("clawline", "Clawline", ["left", "right"]),
        ("silk-soar", "Silk Soar", []),
        ("heal", "Heal", [])
    ];

    private readonly RoomAuditSnapshot snapshot;
    private readonly Dictionary<string, AuditControl> controls = new(StringComparer.Ordinal);
    private readonly Dictionary<AuditAtom, Rule> bindings = [];
    public IReadOnlyCollection<AuditControl> Controls => controls.Values;
    public IReadOnlyList<AuditStart> Starts { get; }
    public RoomAuditSnapshot Snapshot => snapshot;
    private sealed record Rule(Func<AuditScenario, IReadOnlySet<Guid>, bool> Pass, string Explanation, bool Unresolved = false,
        Rule[]? Children = null, bool Alternatives = false, string? ControlKey = null, bool Manual = false, Guid? LocalCheckId = null)
    {
        public AuditPrerequisite Evidence(AuditScenario s, IReadOnlySet<Guid> c) =>
            new(Pass(s, c), Explanation, Unresolved, Alternatives, Children?.Select(x => x.Evidence(s, c)).ToArray() ?? []);
    }

    public RoomAuditEvaluator(RoomAuditSnapshot snapshot)
    {
        this.snapshot = snapshot;
        foreach (var row in Movement)
        {
            if (row.Directions.Length == 0) Add($"move:{row.Key}", row.Label, "Movement");
            foreach (var direction in row.Directions) Add($"move:{row.Key}:{direction}", $"{row.Label} {direction}", "Movement");
        }
        foreach (var crest in snapshot.Crests)
        {
            Add(ValueKey("crest", crest), crest.Name, "Crests");
            Add(ValueKey("crest-level", crest), crest.Name, "Crests", true);
        }
        var starts = snapshot.Rows.Where(x => x.Kind == "exit").Select(x =>
            new AuditStart($"exit:{x.Id}", $"Entrance: {x.Name}{(Node(x.SourceId) is null ? " — unresolved assignment" : "")}", Node(x.SourceId), Node(x.SourceId) is not null)).ToList();
        starts.AddRange(snapshot.Subrooms.Select(x => new AuditStart($"subroom:{x.Id}", $"Subroom: {x.Name}", x.Id, true)));
        if (snapshot.Subrooms.Count == 0) starts.Add(new("room", "Implicit room", snapshot.RoomId, true));
        Starts = starts;
        foreach (var atom in snapshot.Rows.SelectMany(x => Atoms(x.Requirement.Expression)))
            bindings.TryAdd(atom, Bind(atom));
    }

    public static IEnumerable<AuditAtom> Atoms(AuditExpression? expression) => expression switch
    {
        AuditAtom atom => [atom],
        AuditBoolean boolean => Atoms(boolean.Left).Concat(Atoms(boolean.Right)),
        _ => []
    };
    public AuditScenario Reconcile(AuditScenario? retained)
    {
        var result = new AuditScenario();
        if (retained is null) return result;
        result.Start = Starts.Any(x => x.Valid && x.Key == retained.Start) ? retained.Start : "";
        result.Act = retained.Act is >= 1 and <= 3 ? retained.Act : 1;
        result.SoulRandomizer = retained.SoulRandomizer;
        foreach (var control in controls.Values)
            if (control.Difficulty) { var level = retained.Levels?.GetValueOrDefault(control.Key) ?? 0; result.Levels[control.Key] = level is >= 0 and <= 4 ? level : 0; }
            else result.Facts[control.Key] = retained.Facts?.GetValueOrDefault(control.Key) == true;
        Normalize(result);
        return result;
    }
    public static void SetFact(AuditScenario scenario, string key, bool value)
    {
        scenario.Facts[key] = value;
        if (key == "move:dash:run" && !value)
        { scenario.Facts["move:dash:left"] = false; scenario.Facts["move:dash:right"] = false; }
        Normalize(scenario);
    }
    private static void Normalize(AuditScenario scenario)
    {
        if (scenario.Has("move:dash:left") || scenario.Has("move:dash:right")) scenario.Facts["move:dash:run"] = true;
    }
    private Guid? Node(Guid? id) => snapshot.Subrooms.Count == 0 ? snapshot.RoomId :
        id is { } value && snapshot.Subrooms.Any(x => x.Id == value) ? value : null;

    public AuditEvaluation Evaluate(AuditScenario scenario)
    {
        var reached = new HashSet<Guid>();
        var completed = new HashSet<Guid>();
        var nodes = new Dictionary<Guid, AuditProof>();
        var checks = new Dictionary<Guid, AuditProof>();
        var order = 0;
        var start = Starts.FirstOrDefault(x => x.Valid && x.Key == scenario.Start);
        if (start is { NodeId: { } seed })
        { reached.Add(seed); nodes.Add(seed, new(seed, null, null, new Dictionary<AuditAtom, AuditLeafResult>(), new Dictionary<Guid, AuditProof>(), order++)); }
        IReadOnlyDictionary<AuditAtom, AuditLeafResult> Leaves(AuditRow row) => Atoms(row.Requirement.Expression).Distinct().ToDictionary(x => x, x =>
        {
            var rule = bindings[x];
            return new AuditLeafResult(rule.Pass(scenario, completed), rule.Explanation, rule.Unresolved,
                rule.Evidence(scenario, completed), rule.ControlKey, rule.Manual, rule.LocalCheckId);
        });
        AuditProof Proof(AuditRow row, Guid source)
        {
            var used = SuccessfulAtoms(row.Requirement.Expression, scenario, completed).ToHashSet();
            return new(source, row, nodes[source], Leaves(row).ToDictionary(x => x.Key, x => x.Value with { WitnessUsed = used.Contains(x.Key) }),
                used.Where(x => bindings[x].LocalCheckId is not null).Select(x => bindings[x].LocalCheckId!.Value)
                    .Distinct().ToDictionary(x => x, x => checks[x]), order++);
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var row in snapshot.Rows)
            {
                if (Node(row.SourceId) is not { } source || !reached.Contains(source) || !Pass(row.Requirement.Expression, scenario, completed)) continue;
                if (row.Kind == "connection" && snapshot.Subrooms.Count > 0 && Node(row.DestinationId) is { } destination && !reached.Contains(destination))
                { var proof = Proof(row, source); reached.Add(destination); nodes.Add(destination, proof); changed = true; }
                else if (row.Kind == "check" && !completed.Contains(row.Id))
                { var proof = Proof(row, source); completed.Add(row.Id); checks.Add(row.Id, proof); changed = true; }
            }
        } while (changed);
        var results = new Dictionary<(string Kind, Guid Id), AuditRowResult>();
        foreach (var row in snapshot.Rows)
        {
            var source = Node(row.SourceId);
            var valid = source is not null && (row.Kind != "connection" || snapshot.Subrooms.Count > 0 && Node(row.DestinationId) is not null);
            var access = !valid ? "Unresolved subroom assignment" : start is null ? "Select origin…" : !reached.Contains(source!.Value) ? "Subroom not reached" : "Subroom reached";
            var leaves = Leaves(row);
            var gate = Pass(row.Requirement.Expression, scenario, completed);
            results[(row.Kind, row.Id)] = new(valid && reached.Contains(source!.Value) && gate, access, leaves, gate);
        }
        return new(reached, completed, results, start is not null, nodes, checks);
    }
    private IEnumerable<AuditAtom> SuccessfulAtoms(AuditExpression? expression, AuditScenario scenario, IReadOnlySet<Guid> completed) => expression switch
    {
        AuditAtom atom when Pass(atom, scenario, completed) => [atom],
        AuditBoolean { IsAnd: true } both => SuccessfulAtoms(both.Left, scenario, completed).Concat(SuccessfulAtoms(both.Right, scenario, completed)),
        AuditBoolean either => SuccessfulAtoms(Pass(either.Left, scenario, completed) ? either.Left : either.Right, scenario, completed),
        _ => []
    };
    public Guid? SourceNode(AuditRow row) => Node(row.SourceId);
    public string NodeName(Guid? id) => snapshot.Subrooms.Count == 0 && id == snapshot.RoomId ? "Room" :
        snapshot.Subrooms.FirstOrDefault(x => x.Id == id)?.Name ?? "Unresolved subroom";
    public string RowName(AuditRow row) => row.Kind == "connection" ? $"{row.Name}: {NodeName(row.SourceId)} → {NodeName(row.DestinationId)}" : row.Name;
    public string? StartFor(AuditTarget target) => Starts.FirstOrDefault(x => x.Valid && x.Key == (target.Kind == "room" ? "room" : $"{target.Kind}:{target.Id}"))?.Key;
    private bool Pass(AuditExpression? expression, AuditScenario scenario, IReadOnlySet<Guid> completed) => expression switch
    {
        AuditAtom atom => bindings[atom].Pass(scenario, completed),
        AuditBoolean { IsAnd: true } and => Pass(and.Left, scenario, completed) && Pass(and.Right, scenario, completed),
        AuditBoolean or => Pass(or.Left, scenario, completed) || Pass(or.Right, scenario, completed),
        _ => false
    };
    private void Add(string key, string label, string group, bool difficulty = false) => controls.TryAdd(key, new(key, label, group, difficulty));
    public static string ValueKey(string kind, AuditValue value) => $"{kind}:{value.Id:D}:{value.OutputValue}";
    private Rule Fact(string key, string label, string group, bool manual = false)
    { Add(key, label, group); return new((s, _) => s.Has(key), label, ControlKey: key, Manual: manual); }
    private Rule Difficulty(string key, string label, string? authored)
    {
        label = Friendly(label);
        Add($"level:{key}", label, "Technique difficulty", true);
        return new((s, _) => Meets(s.Level($"level:{key}"), authored), $"{label} difficulty ≥ {Friendly(authored ?? "none")}");
    }
    private static bool Meets(int level, string? authored) => authored is null or "none" or "intended" || level >= (authored switch { "easy" => 1, "medium" => 2, "hard" => 3, "cbt" => 4, _ => int.MaxValue });
    private static Rule All(params Rule[] rules) => new((s, c) => rules.All(x => x.Pass(s, c)), "All prerequisites", rules.Any(x => x.Unresolved), rules);
    private static Rule Any(params Rule[] rules) => new((s, c) => rules.Any(x => x.Pass(s, c)), "Any alternative", rules.Any(x => x.Unresolved), rules, true);
    private static Rule Block(string reason) => new((_, _) => false, reason, true);
    private Rule ExactValue(IReadOnlyList<AuditValue> catalogue, string output, string kind, Func<AuditValue, Rule> bind)
    {
        var matches = catalogue.Where(x => x.OutputValue == output).ToArray();
        return matches.Length == 1 ? bind(matches[0]) : Block($"Required {kind} is {(matches.Length == 0 ? "missing" : "ambiguous")}; correct the catalogue separately");
    }
    private Rule Kit(string output) => ExactValue(snapshot.Items, output, "Item", value => Fact(ValueKey("kit", value), value.Name, "Tools / spells / kit"));
    private Rule Soul(string output) => ExactValue(snapshot.Souls, output, "Soul", Soul);
    private Rule Soul(AuditValue value)
    {
        var key = ValueKey("soul", value); Add(key, value.Name, "Souls");
        return Any(new((s, _) => !s.SoulRandomizer, "Soul Randomizer Off"), new((s, _) => s.Has(key), $"{value.Name} Soul selected", ControlKey: key));
    }
    private Rule Crest(AuditAtom atom)
    {
        var named = atom.Operands.FirstOrDefault(x => x.Role == "crest" && x.Id is not null);
        var crests = named is null ? snapshot.Crests : snapshot.Crests.Where(x => x.Id == named.Id && x.OutputValue == named.Value).ToArray();
        if (named is not null && crests.Count == 0) return Block("Named Crest no longer resolves");
        if (named is not null && snapshot.Crests.Count(x => x.OutputValue == named.Value) != 1) return Block("Named Crest is ambiguous");
        return Any(crests.Select(x => CrestPrerequisites(x, atom.Arguments.Difficulty)).ToArray()) with { Explanation = "Own a qualifying crest" };
    }
    private static Rule CrestPrerequisites(AuditValue crest, string? difficulty) => All(
        new((s, _) => s.Has(ValueKey("crest", crest)), $"Own {crest.Name}"),
        new((s, _) => Meets(s.Level(ValueKey("crest-level", crest)), difficulty), $"{crest.Name} difficulty ≥ {Friendly(difficulty ?? "none")}"));
    private Rule Direction(string movement, string? direction)
    {
        var supported = movement == "needle" ? new[] { "left", "right", "up" } : movement == "dash" ? new[] { "left", "right", "down" } : new[] { "left", "right" };
        Rule Has(string d) => movement == "dash" && d == "down" ? Any(
            new((s, _) => s.Has("move:dash:left"), "Swift Step Left (grants Down)"),
            new((s, _) => s.Has("move:dash:right"), "Swift Step Right (grants Down)")) :
            new((s, _) => s.Has($"move:{movement}:{d}"), $"{Movement.First(x => x.Key == movement).Label} {Friendly(d)}");
        return direction switch
        {
            "both" => All(Has("left"), Has("right")),
            "all" => All(supported.Select(Has).ToArray()),
            "either" => Any(Has("left"), Has("right")),
            null or "none" or "any" => Any(supported.Select(Has).ToArray()),
            _ => supported.Contains(direction) ? Has(direction) : new((_, _) => false, $"{Movement.First(x => x.Key == movement).Label} does not support {Friendly(direction)}")
        };
    }
    public static string Friendly(string value) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '));

    // Finite exact signature table, never a generic capability prefix or Name/Category inference.
    private static readonly Dictionary<string, string> Core = BuildCore();
    private static Dictionary<string, string> BuildCore()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string name, string signature) => map.Add(signature, name);
        foreach (var name in new[] { "sprint", "dash", "ledge-grab", "cling-grip", "clawline" })
            foreach (var suffix in new[] { "", ":{direction}", ":{quantity}", ":{direction}:{quantity}" }) Put(name, $"capability:{name}{suffix}");
        foreach (var name in new[] { "swim", "drifters-cloak", "faydown-cloak", "silk-soar", "heal", "silkspear", "sharpdart", "thread-storm", "rune-rage" })
            foreach (var suffix in new[] { "", ":{quantity}" }) Put(name, $"capability:{name}{suffix}");
        foreach (var name in new[] { "think", "look", "move", "jump", "taunt", "naked-pogo" })
            foreach (var suffix in new[] { "", ":{direction}", ":{difficulty}" }) Put("ignored", $"capability:{name}{suffix}");
        foreach (var name in new[] { "break-vines", "break-wall", "break-blast-rock", "break-airlock", "break-switch" }) Put("break", $"capability:{name}:{{direction}}");
        foreach (var name in new[] { "scuttlebrace", "shaman-scuttler", "drill-crystal-pogo", "wind-skip", "cocoon-skip", "damage-boost", "hazard-respawn", "enemy-pogo" })
            foreach (var suffix in new[] { ":{difficulty}", ":{difficulty}:{quantity}" }) Put(name, $"capability:{name}{suffix}");
        Put("enemy-type-pogo", "capability:enemy-type-pogo:{soul}:{difficulty}");
        Put("damage-soul", "technique:damage-boost:{soul}:{difficulty}");
        Put("stall", "capability:stall:{method}:{difficulty}");
        Put("stall", "capability:stall:{method}:{difficulty}:{quantity}");
        Put("crest-pogo", "capability:crest-pogo:{crest}:{difficulty}");
        Put("needle-strike", "capability:needle-strike:{crest}:{difficulty}");
        Put("needle-attack", "capability:needle:{crest}:{direction}");
        Put("soul", "soul:{soul}"); Put("barnak", "macro:barnak-elevator");
        Put("have", "item:{item}"); Put("dependency", "dependency:{check}");
        Put("none", "none"); Put("invalid", "invalid"); Put("invalid", "impassable");
        foreach (var setting in new[] { "steel-soul", "bellshrinesanity", "bossanity", "wishsanity", "entrance-randomizer", "advanced-knowledge" })
            foreach (var state in new[] { "on", "off" }) Put($"setting/{setting}/{state}", $"setting:{(setting == "advanced-knowledge" ? "elite-ball-knowledge" : setting)}-{state}");
        foreach (var act in new[] { "1", "2", "3" }) Put($"act/{act}", $"state:act-{act}");
        Put("normal-world", "state:enemy-spawn-normal-world"); Put("black-thread", "state:enemy-spawn-black-thread-world");
        return map;
    }
    private Rule Bind(AuditAtom atom)
    {
        if (!Core.TryGetValue(atom.Signature, out var kind)) return Manual(atom);
        var a = atom.Arguments;
        Rule rule;
        switch (kind)
        {
            case "none": rule = new((_, _) => true, "Explicit none"); break;
            case "invalid": rule = new((_, _) => false, "Explicit invalid / impassable"); break;
            case "ignored": rule = new((_, _) => true, "ignored in initial audit"); break;
            case "sprint": rule = new((s, _) => s.Has("move:dash:run"), "Swift Step Run (direction does not split sprint)"); break;
            case "dash": case "ledge-grab": case "cling-grip": case "clawline": rule = Direction(kind, a.Direction); break;
            case "swim": case "drifters-cloak": case "faydown-cloak": case "silk-soar": case "heal":
                rule = new((s, _) => s.Has($"move:{kind}"), Movement.First(x => x.Key == kind).Label); break;
            case "silkspear": case "sharpdart": case "thread-storm": case "rune-rage": rule = Kit(SpellItem(kind)); break;
            case "scuttlebrace": rule = All(Kit("tool-scuttlebrace"), Direction("dash", "down"), Difficulty(kind, "Scuttlebrace", a.Difficulty)); break;
            case "shaman-scuttler":
                rule = All(ExactValue(snapshot.Crests, "shaman", "Crest", crest => CrestPrerequisites(crest, a.Difficulty)), Difficulty("scuttlebrace", "Scuttlebrace", a.Difficulty)); break;
            case "drill-crystal-pogo": rule = All(Kit("tool-delver-s-drill"), Kit("tool-memory-crystal"), Difficulty(kind, "Drill Crystal Pogo", a.Difficulty)); break;
            case "wind-skip": case "cocoon-skip": case "damage-boost": case "hazard-respawn": case "enemy-pogo": rule = Difficulty(kind, kind, a.Difficulty); break;
            case "enemy-type-pogo": case "damage-soul":
                rule = All(Difficulty(kind == "enemy-type-pogo" ? "enemy-pogo" : "damage-boost", kind == "enemy-type-pogo" ? "Enemy Pogo" : "Damage Boost", a.Difficulty), TypedSoul(atom)); break;
            case "crest-pogo": rule = Crest(atom); break;
            case "needle-strike": rule = All(Crest(atom), Kit("ability-needle-strike")); break;
            case "needle-attack": rule = All(Crest(atom), Direction("needle", a.Direction)); break;
            case "break": rule = All(Crest(atom), Direction("needle", a.Direction)); break;
            case "soul": rule = TypedSoul(atom); break;
            case "barnak": rule = Soul("Swamp Barnacle"); break;
            case "stall": rule = All(Difficulty("stall", "Stall", a.Difficulty), Method(atom)); break;
            case "have":
                var item = atom.Operands.FirstOrDefault(x => x.Role == "item");
                var value = snapshot.Items.SingleOrDefault(x => item is not null && x.Id == item.Id && x.OutputValue == item.Value);
                rule = value is null ? Block("Item operand no longer resolves") : KnownKitOutputs.Contains(value.OutputValue) ? Kit(value.OutputValue) : Fact(ValueKey("item", value), $"Have {value.Name}", "Other prerequisites", true); break;
            case "dependency":
                var check = snapshot.Checks.SingleOrDefault(x => x.Id == atom.DependencyId);
                rule = check is null ? Block("Check dependency no longer resolves") : check.RoomId == snapshot.RoomId ?
                    new((_, completed) => completed.Contains(check.Id), $"Local check: {check.Name}", LocalCheckId: check.Id) :
                    Fact($"external:{check.Id:D}:{check.RoomId:D}:{check.Name}", $"{check.RoomName} — {check.Name}", "External dependencies"); break;
            case "normal-world": rule = new((s, _) => s.Act is 1 or 2, "Normal-world spawn: Act I or II"); break;
            case "black-thread": rule = new((s, _) => s.Act == 3, "Black-thread spawn: Act III"); break;
            default:
                if (kind.StartsWith("setting/", StringComparison.Ordinal))
                {
                    var parts = kind.Split('/'); var key = $"setting:{parts[1]}"; Add(key, Friendly(parts[1]), "Settings");
                    rule = new((s, _) => s.Has(key) == (parts[2] == "on"), $"{Friendly(parts[1])} {Friendly(parts[2])}");
                }
                else if (kind.StartsWith("act/", StringComparison.Ordinal))
                { var act = int.Parse(kind[4..]); rule = new((s, _) => s.Act == act, $"Act {act}"); }
                else throw new InvalidOperationException("Unbound finite audit mapping");
                break;
        }
        return a.Quantity is null ? rule : rule with { Explanation = $"{rule.Explanation}; quantity {a.Quantity} not evaluated" };
    }
    private Rule TypedSoul(AuditAtom atom)
    {
        var operand = atom.Operands.FirstOrDefault(x => x.Role == "soul");
        var value = snapshot.Souls.SingleOrDefault(x => operand is not null && x.Id == operand.Id && x.OutputValue == operand.Value);
        return value is null ? Block("Soul operand no longer resolves") : ExactValue(snapshot.Souls, value.OutputValue, "Soul", Soul);
    }
    private Rule Method(AuditAtom atom)
    {
        var operand = atom.Operands.FirstOrDefault(x => x.Role == "method");
        var value = snapshot.Methods.SingleOrDefault(x => operand is not null && x.Id == operand.Id && x.OutputValue == operand.Value);
        if (value is null) return Block("Method operand no longer resolves");
        if (snapshot.Methods.Count(x => x.OutputValue == value.OutputValue) != 1) return Block($"{value.Name} Method is ambiguous");
        if (value.OutputValue == "heal") return All(new((s, _) => s.Has("move:heal"), "Heal available"),
            Any(snapshot.Crests.Where(x => x.OutputValue != "shaman").Select(x => new Rule((s, _) => s.Has(ValueKey("crest", x)), $"Own {x.Name}")).ToArray())
                with { Explanation = "Any owned non-Shaman Crest (no crest difficulty gate)" });
        return MethodKit.TryGetValue(value.OutputValue, out var item) ? Kit(item) : Fact(ValueKey("method-kit", value), value.Name, "Tools / spells / kit");
    }
    private static string SpellItem(string name) => name switch { "silkspear" => "silk-skill-silkspear", "sharpdart" => "silk-skill-sharpdart", "thread-storm" => "silk-skill-thread-storm", "rune-rage" => "silk-skill-rune-rage", _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    private static readonly Dictionary<string, string> MethodKit = new(StringComparer.Ordinal)
    {
        ["silkspear"] = "silk-skill-silkspear", ["sharpdart"] = "silk-skill-sharpdart", ["thread-storm"] = "silk-skill-thread-storm", ["rune-rage"] = "silk-skill-rune-rage",
        ["flintslate"] = "tool-flintslate", ["voltvessels"] = "tool-voltvessels", ["flea-brew"] = "tool-flea-brew",
        ["pimpillo"] = "tool-pimpillo", ["longpin"] = "tool-longpin", ["delvers-drill"] = "tool-delver-s-drill", ["memory-crystal"] = "tool-memory-crystal", ["scuttlebrace"] = "tool-scuttlebrace"
        , ["plasmium-phial"] = "tool-plasmium-phial", ["cross-stitch"] = "silk-skill-cross-stitch", ["pale-nails"] = "silk-skill-pale-nails"
    };
    private static readonly HashSet<string> KnownKitOutputs = [.. MethodKit.Values, "ability-needle-strike"];
    private Rule Manual(AuditAtom atom)
    {
        var key = "manual:" + JsonSerializer.Serialize(new { atom.PredicateId, atom.Signature, atom.Arguments, Operands = atom.Operands.Select(x => new { x.Role, x.Id, x.Value }), atom.DependencyId });
        var row = snapshot.Rows.First(x => Atoms(x.Requirement.Expression).Contains(atom));
        var text = row.Requirement.Source.Substring(atom.Start, atom.Length);
        return Fact(key, text, "Other prerequisites", true);
    }
}
