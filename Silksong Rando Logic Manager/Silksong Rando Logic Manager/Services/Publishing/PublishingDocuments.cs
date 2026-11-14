using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed class PublishingDocuments
{
    internal List<RoomGroup> Groups = [];
    internal List<Room> Rooms = [];
    internal List<Subroom> Subrooms = [];
    internal List<RoomTransition> Transitions = [];
    internal List<SubroomConnection> Connections = [];
    internal List<CheckLocation> Checks = [];
    internal Dictionary<Guid, string> Paths = [];
    internal Dictionary<Guid, string> GroupPaths = [];
    private List<RequirementSoul> souls = [];
    internal static async Task<PublishingDocuments> LoadAsync(PublishingSnapshotFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var d = new PublishingDocuments
        {
            Groups = await db.RoomGroups.OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Rooms = await db.Rooms.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Subrooms = await db.Subrooms.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Transitions = await db.RoomTransitions.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Connections = await db.SubroomConnections.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Checks = await db.CheckLocations.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            souls = await db.RequirementSouls.ToListAsync()
        };
        var groupNames = new HashSet<string>(StringComparer.Ordinal) { "00-annotations", "01-geometry", ".git" };
        foreach (var group in d.Groups)
        {
            d.GroupPaths[group.Id] = Unique(group.FriendlyName, groupNames);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in d.Rooms.Where(x => x.RoomGroupId == group.Id))
                d.Paths[room.Id] = d.GroupPaths[group.Id] + "/" + Unique(room.FriendlyName, names) + ".md";
        }
        var ungrouped = d.Rooms.Where(x => !d.Paths.ContainsKey(x.Id)).ToArray();
        if (ungrouped.Length != 0) throw new PublishingBlock("Active rooms have no matching room group in the frozen publication snapshot:\n" +
            string.Join("\n", ungrouped.Select(x => $"{x.FriendlyName} (room {x.Id:D}; group {x.RoomGroupId})")) +
            "\nAssign these rooms to existing room groups in the editor, then discard the failed preparation and prepare again.");
        return d;
    }
    private static string Unique(string name, HashSet<string> used)
    {
        var stem = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length == 0) stem = "untitled";
        var result = stem; var number = 2;
        while (!used.Add(result)) result = stem + "-" + number++;
        return result;
    }
    internal static string Title(Room r) => Regex.Replace(r.FriendlyName + (string.IsNullOrEmpty(r.InGameId) ? "" : $" ({r.InGameId})"), @"\s+", " ").Trim();
    internal List<string> Validate()
    {
        var issues = new List<string>();
        var protectedNames = new HashSet<string>(["driftlin", "mnemonid", "mnemonord", "skullwing", "wingmould", "barnak"]);
        static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        var aliases = protectedNames.ToDictionary(x => x, x => x);
        foreach (var soul in souls.Where(x => protectedNames.Contains(Normalize(x.Name))))
            foreach (var alias in soul.Aliases.Split(',').Where(x => !string.IsNullOrWhiteSpace(x))) aliases[Normalize(alias)] = Normalize(soul.Name);
        var pogo = new Regex(@"\b(?:(?<difficulty>(?!enemy\b)[a-z]+)\s+)?(?:enemy\s+)?pogo\s+off\s+(?<enemies>\[[^\]]+\]|(?:(?!\s+\b(?:AND|OR)\b)[^()\r\n])+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var room in Rooms)
        {
            foreach (var check in Checks.Where(x => x.RoomId == room.Id && new[] { "boss", "gauntlet" }.Contains(x.LocationType?.Trim().ToLowerInvariant())))
                if (!Regex.IsMatch(check.Requirements, @"(?<!\w)Combat\s+Requirements(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    issues.Add($"{Title(room)}: {check.FriendlyName} [{check.LocationType}] must include Combat Requirements.");
            var rows = Transitions.Where(x => x.RoomId == room.Id).Select(x => (x.FriendlyName, x.Requirements, Kind: "transition"))
                .Concat(Connections.Where(x => x.RoomId == room.Id).Select(x => (x.FriendlyName, x.Requirements, Kind: "connection")))
                .Concat(Checks.Where(x => x.RoomId == room.Id).Select(x => (x.FriendlyName, x.Requirements, Kind: "location")));
            foreach (var row in rows)
                foreach (Match match in pogo.Matches(row.Requirements))
                {
                    var names = match.Groups["enemies"].Value.Trim('[', ']').Split(',').Select(Normalize).Where(aliases.ContainsKey).Select(x => aliases[x]).Distinct().Order(StringComparer.Ordinal).ToArray();
                    if (names.Length > 0 && !match.Groups["difficulty"].Value.Equals("intended", StringComparison.OrdinalIgnoreCase))
                        issues.Add($"{Title(room)}: {row.FriendlyName} [{row.Kind}]: {string.Join(", ", names)} pogo-off must use Intended.");
                }
        }
        return issues;
    }
    internal void Write(PublishingStorage storage, string directory, string captures, string font, Action<string?, int, int> progress, PublishingTimings timings)
    {
        var index = new StringBuilder("# Silksong Randomizer Notes\n\nGenerated from the non-archived records in the accompanying frozen SQLite snapshot.\n\n[Composite Master](./MASTER.md)\n\n");
        var master = new StringBuilder("# Silksong Randomizer Logic\n\nGenerated from the database alongside the individual room pages.\n\n");
        var count = 0;
        foreach (var group in Groups)
        {
            index.Append($"## {group.FriendlyName}\n\n"); master.Append($"## {group.FriendlyName}\n\n");
            var rooms = Rooms.Where(x => x.RoomGroupId == group.Id).ToArray();
            foreach (var room in rooms)
            {
                progress(Title(room), count, Rooms.Count);
                var taskTiming = storage.Timing;
                using var timing = timings.Begin("Room generation", room.Id.ToString("D"), taskTiming);
                storage.Timing = timing;
                try
                {
                var images = PublishingRenderer.Render(storage, this, room, captures, directory, font, timing);
                var markdown = timing.Measure(PublishingMetric.Markdown, () => Markdown(room, images, false));
                WriteText(storage, directory, Paths[room.Id], markdown);
                using var masterTime = timing.Segment(PublishingMetric.Master);
                master.Append($"<a name=\"{Anchor(Paths[room.Id])}\"></a>\n\n");
                foreach (var line in Markdown(room, images, true).TrimEnd().Split('\n'))
                    master.Append(line.StartsWith('#') ? "##" + line : line).Append('\n');
                master.Append('\n');
                index.Append($"- [{Title(room)}](./{Paths[room.Id]})\n"); count++;
                masterTime.Complete();
                timing.Complete();
                }
                finally { storage.Timing = taskTiming; }
            }
            if (rooms.Length == 0) index.Append("No rooms defined.\n");
            index.Append('\n');
        }
        index.Append("## Image Credits\n\nMassive thank you to **RainingChain** and **IdoManti** for allowing us to\ngenerate room annotations using the 25%-resolution image of their amazing\nhigh-detail Silksong map. The room images in this documentation are generated\nfrom that map through Silksong Rando Logic Manager.\n\n[Explore their interactive Silksong map and their incredible work.](https://scripterswar.com/silksong/map)\n\nThe underlying map imagery is not covered by this repository's license.\nReuse, modification, or redistribution of that imagery requires permission\nfrom its owners.\n");
        progress(null, count, Rooms.Count);
        WriteIndex("README.md", () => index.ToString());
        WriteIndex("MASTER.md", () => master.ToString().TrimEnd() + "\n");
        void WriteIndex(string path, Func<string> generate)
        {
            var taskTiming = storage.Timing;
            using var timing = timings.Begin(path + " final generation and write", parent: taskTiming);
            storage.Timing = timing;
            try
            {
                var text = timing.Measure(path == "README.md" ? PublishingMetric.Markdown : PublishingMetric.Master, generate);
                WriteText(storage, directory, path, text); timing.Complete();
            }
            finally { storage.Timing = taskTiming; }
        }
    }
    internal static void WriteText(PublishingStorage storage, string directory, string path, string value)
    {
        var destination = PublishingStorage.Resolve(directory, path);
        var bytes = storage.Timing is { } timing ? timing.Measure(PublishingMetric.TextEncoding, () => new UTF8Encoding(false).GetBytes(value)) : new UTF8Encoding(false).GetBytes(value);
        storage.Atomic(destination, bytes);
    }
    private static string Anchor(string path) => "room-" + path[..^3].Replace("/", "--");
    private static string Cell(object? value) => (value?.ToString() ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ").Trim();
    private static string Verified(bool? value) => value is true ? "Verified" : value is false ? "Needs verification" : "";
    private string SubroomName(Guid? id, string? fallback) => Subrooms.FirstOrDefault(x => x.Id == id)?.FriendlyName ?? fallback ?? "";
    private string Markdown(Room room, PublishingImageResult images, bool master)
    {
        var b = new StringBuilder($"# {Title(room)}\n\n");
        if (master) b.Append($"[View Room Page](./{Paths[room.Id]})\n\n");
        if (!string.IsNullOrEmpty(room.InGameId)) b.Append($"**Game ID:** {room.InGameId}\n\n");
        if (!string.IsNullOrEmpty(room.Contributors)) b.Append($"**Contributors:** {room.Contributors}\n\n");
        string Mark(Guid id) => images.Annotated.Contains(id) ? "✓" : "";
        void Table(string title, string[] headers, IEnumerable<object?[]> rows, string empty)
        {
            b.Append($"## {title}\n\n"); var entries = rows.ToArray();
            if (entries.Length == 0) b.Append(empty).Append('\n');
            else
            {
                b.Append("| ").AppendJoin(" | ", headers).Append(" |\n| ").AppendJoin(" | ", headers.Select(_ => "---")).Append(" |\n");
                foreach (var row in entries) b.Append("| ").AppendJoin(" | ", row.Select(Cell)).Append(" |\n");
            }
            b.Append('\n');
        }
        var subs = Subrooms.Where(x => x.RoomId == room.Id).ToArray();
        Table("Subrooms", ["No.", "Subroom", "Annotated"], subs.Select((x, i) => new object?[] { $"S{i + 1}", x.FriendlyName, Mark(x.Id) }), "No subrooms defined.");
        foreach (var s in subs.Where(x => !string.IsNullOrEmpty(x.Notes))) b.Append($"- **{s.FriendlyName}:** {s.Notes}\n");
        if (subs.Any(x => !string.IsNullOrEmpty(x.Notes))) b.Append('\n');
        string Destination(RoomTransition t)
        {
            var target = Rooms.FirstOrDefault(x => x.Id == t.ResolvedDestinationRoomId);
            if (target is null) return string.IsNullOrEmpty(t.DestinationRoomReferenceText) ? "TODO" : t.DestinationRoomReferenceText;
            var path = Paths[target.Id];
            return $"[{Title(target)}]({(master ? "#" + Anchor(path) : target.RoomGroupId == room.RoomGroupId ? path.Split('/')[1] : "../" + path)})";
        }
        Table("Room Transitions", ["Alias", "Name", "From subroom", "Destination", "Destination alias", "Requirements", "TODO", "Verification", "Annotated", "Notes"],
            Transitions.Where(x => x.RoomId == room.Id).Select(x => new object?[] { x.Alias, x.FriendlyName, SubroomName(x.ResolvedSourceSubroomId, x.SourceSubroomReferenceText), Destination(x), x.DestinationTransitionAliasText, x.Requirements, x.IsTodo ? "TODO" : "", Verified(x.IsVerified), Mark(x.Id), x.Notes }), "No room transitions defined.");
        Table("Subroom Connections", ["Alias", "Name", "Source", "Destination", "Requirements", "TODO", "Verification", "Annotated", "Notes"],
            Connections.Where(x => x.RoomId == room.Id).Select(x => new object?[] { x.Alias, x.FriendlyName, SubroomName(x.ResolvedSourceSubroomId, x.SourceSubroomReferenceText), SubroomName(x.ResolvedDestinationSubroomId, x.DestinationSubroomReferenceText), x.Requirements, x.IsTodo ? "TODO" : "", Verified(x.IsVerified), Mark(x.Id), x.Notes }), "No subroom connections defined.");
        Table("Check Locations", ["No.", "Check", "Subroom", "Requirements", "TODO", "Verification", "Location Type", "Annotated", "Notes"],
            Checks.Where(x => x.RoomId == room.Id).Select((x, i) => new object?[] { i + 1, x.FriendlyName, SubroomName(x.ResolvedSubroomId, x.SubroomReferenceText), x.Requirements, x.IsTodo ? "TODO" : "", Verified(x.IsVerified), x.LocationType, Mark(x.Id), x.Notes }), "No check locations defined.");
        if (!string.IsNullOrEmpty(room.Comments)) b.Append($"## Notes\n\n{room.Comments.Trim()}\n\n");
        if (!master)
        {
            b.Append("## Room Images\n\n");
            if (images.Paths.Count == 0) b.Append("Scene image unavailable.\n");
            foreach (var (kind, label) in new[] { ("connections", "Connections"), ("checks", "Checks"), ("scene", "Scene") })
                if (images.Paths.TryGetValue(kind, out var path)) b.Append($"### {label}\n\n[![{label} for {Cell(Title(room))}](../{path})](../{path})\n\n");
        }
        return b.ToString().TrimEnd() + "\n";
    }
}
