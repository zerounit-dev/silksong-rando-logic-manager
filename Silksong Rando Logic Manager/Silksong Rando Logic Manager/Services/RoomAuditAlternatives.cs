namespace Silksong_Rando_Logic_Manager.Services;

// Immutable, bound scenario constraints. Value is a Boolean, a lower bound, or an Act bit set.
public sealed record AuditCondition(string Kind, string Key, string Label, int Value, bool Manual = false)
{
    public bool Matches(AuditScenario scenario) => Kind switch
    {
        "level" => scenario.Level(Key) >= Value,
        "act" => (Value & (1 << (scenario.Act - 1))) != 0,
        "soul-mode" => scenario.SoulRandomizer == (Value == 1),
        _ => scenario.Has(Key) == (Value == 1)
    };
    public string Summary => Kind switch
    {
        "level" => $"{Label} ≥ {RoomAuditEvaluator.DifficultyNames[Math.Clamp(Value, 0, 4)]}",
        "act" => "Act " + string.Join(" / ", Enumerable.Range(1, 3).Where(x => (Value & (1 << (x - 1))) != 0).Select(x => new[] { "I", "II", "III" }[x - 1])),
        "soul-mode" => $"Soul Randomizer {(Value == 1 ? "On" : "Off")}",
        _ => $"{Label}{(Key.StartsWith("setting:", StringComparison.Ordinal) ? Value == 1 ? " On" : " Off" : "")}{(Manual ? " · manual" : "") }"
    };
}
public sealed record AuditRouteStep(AuditRow Row, IReadOnlyList<AuditCondition> Conditions,
    IReadOnlyList<string> Notices, IReadOnlyList<AuditAtom> UsedAtoms);
public sealed record AuditAlternativeRoute(string Identity, IReadOnlyList<AuditRouteStep> Steps,
    AuditRouteStep? OwnGate, IReadOnlyList<AuditCondition> Conditions);
public sealed record AuditAlternative(string Identity, IReadOnlyList<AuditCondition> Conditions, IReadOnlyList<AuditAlternativeRoute> Routes);
public sealed record AuditAnalysisIssue(AuditRow Row, string Reason);
public sealed record AuditAlternativeAnalysis(bool HasOrigin, IReadOnlyList<AuditAlternative> Alternatives,
    IReadOnlyList<AuditAnalysisIssue> Unresolved, bool Disconnected = false, IReadOnlyList<AuditAnalysisIssue>? Impassable = null);

public sealed partial class RoomAuditEvaluator
{
    // All state of this search is invocation-local. No mutable scenario, EF, UI, or process cache.
    public AuditAlternativeAnalysis Analyze(string startKey, AuditTarget target, CancellationToken token)
    {
        var start = Starts.FirstOrDefault(x => x.Valid && x.Key == startKey);
        if (start?.NodeId is not { } origin) return new(false, [], []);
        var issues = new List<AuditAnalysisIssue>();
        var impassable = new List<AuditAnalysisIssue>();
        var targetRow = snapshot.Rows.FirstOrDefault(x => x.Kind == target.Kind && x.Id == target.Id);
        var routes = targetRow is null
            ? Nodes(target.Id, new HashSet<(string, Guid)>())
            : RowRoutes(targetRow, new HashSet<(string, Guid)>());
        token.ThrowIfCancellationRequested();
        // Subsumption is deliberately route-local. A demanding spatial route is not erased by another.
        var reduced = Reduce(routes, final: true);
        var alternatives = reduced.GroupBy(x => ConditionIdentity(x.Conditions), StringComparer.Ordinal)
            .Select(group => new AuditAlternative(group.Key, group.First().Conditions, group.ToArray())).ToArray();
        var connected = new HashSet<Guid> { origin };
        bool changed;
        do
        {
            token.ThrowIfCancellationRequested(); changed = false;
            foreach (var edge in snapshot.Rows.Where(x => x.Kind == "connection" && snapshot.Subrooms.Count > 0))
                if (Node(edge.SourceId) is { } from && connected.Contains(from) && Node(edge.DestinationId) is { } to) changed |= connected.Add(to);
        } while (changed);
        var targetNode = targetRow is null ? target.Id : Node(targetRow.SourceId);
        return new(true, alternatives, issues.Distinct().ToArray(), targetNode is { } n && !connected.Contains(n), impassable.Distinct().ToArray());

        List<AuditAlternativeRoute> Reduce(IEnumerable<AuditAlternativeRoute> values, bool final = false) => values.Select(value =>
        { token.ThrowIfCancellationRequested(); return value; }).GroupBy(x => final ? RouteShape(x) : x.Identity, StringComparer.Ordinal).SelectMany(group =>
        {
            token.ThrowIfCancellationRequested();
            var unique = group.GroupBy(x => ConditionIdentity(x.Conditions), StringComparer.Ordinal).Select(x => x.First()).ToArray();
            return unique.Where(candidate => !unique.Any(other => { token.ThrowIfCancellationRequested(); return !ReferenceEquals(candidate, other) && Covers(other.Conditions, candidate.Conditions) && (!final || !Covers(candidate.Conditions, other.Conditions)); }));
        }).ToList();

        // Reduction remains local to the same authored directed movement sequence.
        string RouteShape(AuditAlternativeRoute route)
        {
            token.ThrowIfCancellationRequested();
            return "origin" + string.Concat(route.Steps.Concat(route.OwnGate is { } own ? [own] : []).Select(step =>
                $"/{step.Row.Id:D}"));
        }

        List<AuditAlternativeRoute> Nodes(Guid node, HashSet<(string, Guid)> ancestors)
        {
            token.ThrowIfCancellationRequested();
            if (node == origin) return [new("origin", [], null, [])];
            if (ancestors.Contains(("node", node))) return [];
            var next = new HashSet<(string, Guid)>(ancestors) { ("node", node) };
            var result = new List<AuditAlternativeRoute>();
            foreach (var edge in snapshot.Rows.Where(x => x.Kind == "connection" && Node(x.DestinationId) == node && snapshot.Subrooms.Count > 0))
            {
                foreach (var route in RowRoutes(edge, next))
                {
                    token.ThrowIfCancellationRequested();
                    // Destination reachability needs no repeated movement node in its source route.
                    if (route.Steps.Any(x => Node(x.Row.SourceId) == node)) continue;
                    result.Add(route with { Steps = route.Steps.Concat([route.OwnGate!]).ToArray(), OwnGate = null });
                }
            }
            return Reduce(result);
        }
        List<AuditAlternativeRoute> RowRoutes(AuditRow row, HashSet<(string, Guid)> ancestors)
        {
            token.ThrowIfCancellationRequested();
            if (Node(row.SourceId) is not { } source || row.Kind == "connection" && (snapshot.Subrooms.Count == 0 || Node(row.DestinationId) is null))
            { issues.Add(new(row, "Unresolved subroom assignment")); return []; }
            var sources = Nodes(source, ancestors);
            var gates = Expression(row.Requirement.Expression, row);
            var result = new List<AuditAlternativeRoute>();
            foreach (var route in sources)
                foreach (var gate in gates)
                {
                    token.ThrowIfCancellationRequested();
                    if (Combine(route.Conditions, gate.Conditions) is not { } conditions) continue;
                    var step = new AuditRouteStep(row, gate.Conditions, gate.Notices, gate.UsedAtoms ?? []);
                    result.Add(new($"{route.Identity}/{row.Id:D}", route.Steps, step, conditions));
                }
            return Reduce(result);
        }
        List<Choice> Expression(AuditExpression? expression, AuditRow row)
        {
            token.ThrowIfCancellationRequested();
            if (expression is AuditBoolean boolean)
            {
                var left = Expression(boolean.Left, row); var right = Expression(boolean.Right, row);
                return boolean.IsAnd ? Product(left, right) : left.Concat(right).ToList();
            }
            if (expression is AuditAtom atom) return Bound(bindings[atom], row).Select(x => x with { UsedAtoms = [atom] }).ToList();
            issues.Add(new(row, string.Join("; ", row.Requirement.Reasons.DefaultIfEmpty("Unresolved authored requirement"))));
            return [];
        }
        List<Choice> Bound(Rule rule, AuditRow row)
        {
            token.ThrowIfCancellationRequested();
            if (rule.Children is { } children)
            {
                var result = rule.Alternatives ? new List<Choice>() : new List<Choice> { new([], []) };
                foreach (var child in children)
                {
                    var choices = Bound(child, row);
                    result = rule.Alternatives ? result.Concat(choices).ToList() : Product(result, choices);
                }
                return WithNotice(result, rule);
            }
            if (rule.Unresolved) { issues.Add(new(row, rule.Explanation)); return []; }
            if (rule.Condition is { } condition)
                return WithNotice([new(condition.Kind == "level" && condition.Value == 0 ? [] : [condition], [])], rule);
            if (rule.Constant) return WithNotice([new([], [])], rule);
            impassable.Add(new(row, rule.Explanation)); return [];
        }
        List<Choice> WithNotice(List<Choice> choices, Rule rule) => rule.Notices is { } notices
            ? choices.Select(x => x with { Notices = x.Notices.Concat(notices).Distinct().ToArray() }).ToList() : choices;
        List<Choice> Product(List<Choice> left, List<Choice> right)
        {
            var result = new List<Choice>();
            foreach (var a in left) foreach (var b in right)
            {
                token.ThrowIfCancellationRequested();
                if (Combine(a.Conditions, b.Conditions) is { } conditions)
                    result.Add(new(conditions, a.Notices.Concat(b.Notices).Distinct().ToArray(), (a.UsedAtoms ?? []).Concat(b.UsedAtoms ?? []).Distinct().ToArray()));
            }
            return result;
        }
    }
    private sealed record Choice(IReadOnlyList<AuditCondition> Conditions, IReadOnlyList<string> Notices, IReadOnlyList<AuditAtom>? UsedAtoms = null);
    public static string ConditionIdentity(IEnumerable<AuditCondition> conditions) => string.Join("|", conditions.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Kind}:{x.Key.Length}:{x.Key}:{x.Value}"));
    public static IReadOnlyList<AuditCondition>? Combine(IEnumerable<AuditCondition> a, IEnumerable<AuditCondition> b)
    {
        var combined = new Dictionary<(string, string), AuditCondition>();
        foreach (var item in a.Concat(b))
        {
            var key = (item.Kind, item.Key);
            if (!combined.TryGetValue(key, out var previous)) { combined.Add(key, item); continue; }
            var value = item.Kind switch { "level" => Math.Max(previous.Value, item.Value), "act" => previous.Value & item.Value, _ => previous.Value == item.Value ? item.Value : -1 };
            if (value < 0 || item.Kind == "act" && value == 0) return null;
            combined[key] = previous with { Value = value };
        }
        if (combined.ContainsKey(("fact", "move:dash:left")) || combined.ContainsKey(("fact", "move:dash:right")))
            combined.Remove(("fact", "move:dash:run"));
        return combined.Values.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
    }
    public static bool Covers(IReadOnlyList<AuditCondition> weaker, IReadOnlyList<AuditCondition> stronger) => weaker.All(a =>
        stronger.Any(b => a.Kind == b.Kind && a.Key == b.Key && (a.Kind == "level" ? a.Value <= b.Value : a.Kind == "act" ? (a.Value & b.Value) == b.Value : a.Value == b.Value)) ||
        a.Key == "move:dash:run" && stronger.Any(b => b.Key is "move:dash:left" or "move:dash:right" && b.Value == 1));
}
