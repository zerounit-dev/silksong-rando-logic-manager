using System.Text;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Canonical pure applied-value rules shared by retained V2 and the browser room.</summary>
public static class RoomPresentationRules
{
    public sealed record PathwayFact(Guid Id, string Alias, string Name, string? Source, Guid? SourceId, string? Destination, Guid? DestinationId, bool Archived);
    public sealed class PreparedPathways
    {
        private sealed class Group
        {
            public int Count;
            public readonly List<PathwayFact> Small = new(2);
            public readonly Dictionary<(Guid, Guid), int> Directions = [];
            public readonly Dictionary<(Guid, Guid, Guid), int> IdDirections = [];
        }
        private readonly Dictionary<string, Group> groups = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> names = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, int>> nameAliases = new(StringComparer.OrdinalIgnoreCase);

        public PreparedPathways(IEnumerable<PathwayFact> rows)
        {
            foreach (var row in rows)
            {
                if (row.Archived) continue;
                if (Text(row.Name))
                {
                    var name = row.Name.Trim();
                    names[name] = names.GetValueOrDefault(name) + 1;
                    if (Text(row.Alias))
                    {
                        if (!nameAliases.TryGetValue(name, out var aliases)) nameAliases.Add(name, aliases = new(StringComparer.OrdinalIgnoreCase));
                        var alias = row.Alias.Trim();
                        aliases[alias] = aliases.GetValueOrDefault(alias) + 1;
                    }
                }
                if (!Text(row.Alias)) continue; // Same(blank, blank) is false: EMPTY group.
                var key = row.Alias.Trim();
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new());
                if (++group.Count <= 2) group.Small.Add(row);
                if (row.SourceId is { } source && row.DestinationId is { } destination)
                {
                    var direction = (source, destination);
                    var identity = (source, destination, row.Id);
                    group.Directions[direction] = group.Directions.GetValueOrDefault(direction) + 1;
                    group.IdDirections[identity] = group.IdDirections.GetValueOrDefault(identity) + 1;
                }
            }
        }
        public (V2Severity Severity, string State) Classify(PathwayFact value)
        {
            if (value.Archived) return (V2Severity.Neutral, "unresolved");
            var group = Text(value.Alias) ? groups.GetValueOrDefault(value.Alias.Trim()) : null;
            var sameName = Text(value.Name) ? names.GetValueOrDefault(value.Name.Trim()) : 0;
            var sameNameAlias = Text(value.Name) && Text(value.Alias)
                ? nameAliases.GetValueOrDefault(value.Name.Trim())?.GetValueOrDefault(value.Alias.Trim()) ?? 0 : 0;
            var bad = sameName > sameNameAlias || group?.Count > 2;
            if (group is { Count: <= 2 })
                foreach (var row in group.Small)
                    bad |= !Same(row.Name, value.Name) || !Pair(row, value) || row.Id != value.Id && Direction(row, value);
            var severity = bad ? V2Severity.Danger : V2Severity.Neutral;
            if (value.SourceId is not { } source || value.DestinationId is not { } destination) return (severity, "unresolved");
            var reverse = group is null ? 0 : group.Directions.GetValueOrDefault((destination, source))
                - group.IdDirections.GetValueOrDefault((destination, source, value.Id));
            return (severity, reverse switch { 0 => "one-way", 1 => "bidirectional", _ => "ambiguous reverse match" });
        }
        static bool Direction(PathwayFact a, PathwayFact b) => SameEndpoint(a.Source, a.SourceId, b.Source, b.SourceId) && SameEndpoint(a.Destination, a.DestinationId, b.Destination, b.DestinationId);
        static bool Pair(PathwayFact a, PathwayFact b) => Direction(a, b) || SameEndpoint(a.Source, a.SourceId, b.Destination, b.DestinationId) && SameEndpoint(a.Destination, a.DestinationId, b.Source, b.SourceId);
    }
    public static bool Text(string? x) => !string.IsNullOrWhiteSpace(x);
    public static string Key(string? x) => x!.Trim().ToUpperInvariant();
    public static bool Same(string? a, string? b) => Text(a) && Text(b) && string.Equals(a!.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);
    public static V2Severity Required(string? x) => Text(x) ? V2Severity.Neutral : V2Severity.Warning;
    public static V2Severity Requirements(string? x, bool? succeeded) => succeeded switch { false => V2Severity.Danger, null => V2Severity.Warning, _ => Required(x) };
    public static V2Severity Duplicate(string? x, IEnumerable<string?> xs) => Text(x) && xs.Count(y => Same(x, y)) > 1 ? V2Severity.Danger : Required(x);
    public static V2Severity Alias(string? x, IEnumerable<string?> xs) => !Text(x) ? V2Severity.Warning : x!.Length > 3 || xs.Count(y => Same(x, y)) > 1 ? V2Severity.Danger : V2Severity.Neutral;
    public static V2Severity Presence(string? x, bool has) => has == Text(x) ? V2Severity.Neutral : V2Severity.Warning;
    public static V2Severity Max(V2Severity a, V2Severity b) => (V2Severity)Math.Max((int)a, (int)b);
    public static V2Severity Pair(double? x, double? y) => x is null && y is null || x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b) ? V2Severity.Neutral : V2Severity.Danger;
    public static V2Severity Rectangle(double? x, double? y, double? w, double? h) => x is null && y is null && w is null && h is null || x is double a && y is double b && w is double c && h is double d && double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d) && c > 0 && d > 0 ? V2Severity.Neutral : V2Severity.Danger;
    public static V2Severity ReferenceSeverity(V2Severity normal, V2ReferenceState state) => state is V2ReferenceState.Unresolved or V2ReferenceState.Ambiguous or V2ReferenceState.OutOfSync or V2ReferenceState.TargetArchived ? V2Severity.Danger : normal;
    public static V2ReferenceState Reference(string? text, Guid? id, IEnumerable<(Guid Id, string Text, bool Archived)> candidates)
    {
        if (!Text(text)) return V2ReferenceState.None;
        var matches = candidates.Where(x => Same(x.Text, text)).ToArray(); var active = matches.Where(x => !x.Archived).ToArray();
        if (active.Length == 1) return active[0].Id == id ? V2ReferenceState.Resolved : V2ReferenceState.OutOfSync;
        if (active.Length > 1) return V2ReferenceState.Ambiguous;
        if (matches.Any(x => x.Archived)) return V2ReferenceState.TargetArchived;
        return id is null ? V2ReferenceState.Unresolved : V2ReferenceState.OutOfSync;
    }
    public static bool SameEndpoint(string? a, Guid? aid, string? b, Guid? bid) => aid is not null && bid is not null ? aid == bid : Same(a, b);
    public static string? CanonicalSegment(string? value)
    {
        if (value is null) return null;
        var output = new StringBuilder(); var pendingHyphen = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            var lowered = Rune.ToLowerInvariant(rune);
            if (lowered.IsAscii && (lowered.Value is >= 'a' and <= 'z' or >= '0' and <= '9'))
            {
                if (pendingHyphen && output.Length != 0) output.Append('-');
                output.Append((char)lowered.Value); pendingHyphen = false;
            }
            else pendingHyphen = output.Length != 0;
        }
        return output.Length == 0 ? null : output.ToString();
    }
}
