using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: MasterImporter <master-markdown-path> <database-path>");
    return 2;
}

var masterPath = Path.GetFullPath(args[0]);
var databasePath = Path.GetFullPath(args[1]);
if (!File.Exists(masterPath))
{
    Console.Error.WriteLine($"MASTER.md was not found: {masterPath}");
    return 2;
}

var options = new DbContextOptionsBuilder<LogicDbContext>()
    .UseSqlite($"Data Source={databasePath}")
    .Options;

await using var db = new LogicDbContext(options);
await db.Database.MigrateAsync();
if (await db.RoomGroups.AnyAsync() || await db.Rooms.AnyAsync())
{
    Console.Error.WriteLine("The database already contains logic data. Import was not run to avoid duplicate records.");
    return 3;
}

var importer = new MasterImporter(await File.ReadAllLinesAsync(masterPath));
var result = importer.Parse();
db.RoomGroups.AddRange(result.RoomGroups);
db.Rooms.AddRange(result.Rooms);
db.Subrooms.AddRange(result.Subrooms);
db.RoomTransitions.AddRange(result.Transitions);
db.SubroomConnections.AddRange(result.Connections);
db.CheckLocations.AddRange(result.Checks);
await db.SaveChangesAsync();

var resolutionReport = await new LogicReferenceResolver(db).ResolveAsync();
Console.WriteLine($"Imported {result.RoomGroups.Count} groups, {result.Rooms.Count} rooms, {result.Subrooms.Count} subrooms, {result.Transitions.Count} transitions, {result.Connections.Count} connections, and {result.Checks.Count} checks.");
if (importer.SkippedCheckRows.Count > 0)
{
    Console.WriteLine("Skipped check rows: " + string.Join(" | ", importer.SkippedCheckRows));
}
Console.WriteLine($"Resolver reported {resolutionReport.References.Count(x => x.Status != ReferenceResolutionStatus.Resolved)} non-resolved reference fields.");
return 0;

internal sealed class MasterImporter(string[] lines)
{
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private readonly List<RoomGroup> roomGroups = [];
    private readonly List<Room> rooms = [];
    private readonly List<Subroom> subrooms = [];
    private readonly List<RoomTransition> transitions = [];
    private readonly List<SubroomConnection> connections = [];
    private readonly List<CheckLocation> checks = [];
    private readonly HashSet<string> knownRoomNames = lines
        .Where(x => x.StartsWith("### ", StringComparison.Ordinal))
        .Select(x => x[4..].Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private RoomGroup? currentGroup;
    private Room? currentRoom;
    private Section currentSection;
    private int groupSortOrder;
    private readonly Dictionary<Guid, int> roomSortOrders = [];
    private readonly Dictionary<Guid, int> subroomSortOrders = [];
    private readonly Dictionary<Guid, int> transitionSortOrders = [];
    private readonly Dictionary<Guid, int> connectionSortOrders = [];
    private readonly Dictionary<Guid, int> checkSortOrders = [];
    public List<string> SkippedCheckRows { get; } = [];

    public ImportResult Parse()
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                currentGroup = new RoomGroup { FriendlyName = line[3..].Trim(), SortOrder = groupSortOrder++ };
                roomGroups.Add(currentGroup);
                currentRoom = null;
                currentSection = Section.None;
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                RequireGroup(index);
                var name = line[4..].Trim();
                currentRoom = new Room
                {
                    RoomGroupId = currentGroup!.Id,
                    FriendlyName = name,
                    ReferenceId = ReferenceId(name),
                    SortOrder = Next(roomSortOrders, currentGroup.Id)
                };
                rooms.Add(currentRoom);
                currentSection = Section.None;
                continue;
            }

            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                currentSection = line[5..].Trim() switch
                {
                    "Subrooms" => Section.Subrooms,
                    "Room Transitions" => Section.Transitions,
                    "Subroom Connections" => Section.Connections,
                    "Check Locations" => Section.Checks,
                    "Notes" => Section.Notes,
                    _ => Section.None
                };
                continue;
            }

            if (currentRoom is null)
            {
                continue;
            }

            if (currentSection == Section.Subrooms && line.StartsWith("- ", StringComparison.Ordinal))
            {
                AddSubroom(line[2..].Trim());
                continue;
            }

            if (currentSection == Section.Notes)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    currentRoom.Comments = string.IsNullOrWhiteSpace(currentRoom.Comments)
                        ? line
                        : currentRoom.Comments + Environment.NewLine + line;
                }
                continue;
            }

            if (line.StartsWith("|", StringComparison.Ordinal) && index + 2 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                var headers = ParseCells(line).Select(NormalizeHeader).ToList();
                index += 2;
                while (index < lines.Length && lines[index].StartsWith("|", StringComparison.Ordinal))
                {
                    AddTableRow(currentSection, headers, ParseCells(lines[index]));
                    index++;
                }
                index--;
            }
        }

        return new ImportResult(roomGroups, rooms, subrooms, transitions, connections, checks);
    }

    private void AddSubroom(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return;
        subrooms.Add(new Subroom
        {
            RoomId = currentRoom!.Id,
            FriendlyName = friendlyName,
            ReferenceId = ReferenceId(friendlyName),
            SortOrder = Next(subroomSortOrders, currentRoom.Id)
        });
    }

    private void AddTableRow(Section section, IReadOnlyList<string> headers, IReadOnlyList<string> cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headers.Count; index++)
        {
            row[headers[index]] = index < cells.Count ? cells[index] : string.Empty;
        }

        if (cells.All(string.IsNullOrWhiteSpace)) return;

        switch (section)
        {
            case Section.Transitions:
                AddTransition(row);
                break;
            case Section.Connections:
                AddConnection(row);
                break;
            case Section.Checks:
                AddCheck(row);
                break;
        }
    }

    private void AddTransition(IReadOnlyDictionary<string, string> row)
    {
        var alias = Value(row, "alias");
        var name = Value(row, "name");
        if (string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(name)) return;

        var originalDestination = Value(row, "destination");
        var destination = ParseDestination(originalDestination);
        var notes = Value(row, "notes");
        if (destination.PreserveOriginal)
        {
            notes = AppendImportDetail(notes, $"Imported destination text: {MarkdownLink.Replace(originalDestination, "$1").Trim()}");
        }

        var requirements = Value(row, "requirements");
        transitions.Add(new RoomTransition
        {
            RoomId = currentRoom!.Id,
            Alias = alias,
            FriendlyName = name,
            SourceSubroomReferenceText = EmptyToNull(Value(row, "fromsubroom")),
            DestinationRoomReferenceText = EmptyToNull(destination.RoomName),
            DestinationTransitionAliasText = EmptyToNull(destination.Alias),
            Requirements = requirements,
            Notes = notes,
            IsTodo = ContainsMarker(requirements, notes, "TODO"),
            IsVerified = null,
            SortOrder = Next(transitionSortOrders, currentRoom.Id)
        });
    }

    private void AddConnection(IReadOnlyDictionary<string, string> row)
    {
        var alias = Value(row, "alias");
        var name = Value(row, "name");
        if (string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(name)) return;

        var requirements = Value(row, "requirements");
        var notes = Value(row, "notes");
        connections.Add(new SubroomConnection
        {
            RoomId = currentRoom!.Id,
            Alias = alias,
            FriendlyName = name,
            SourceSubroomReferenceText = Value(row, "source"),
            DestinationSubroomReferenceText = Value(row, "destination"),
            Requirements = requirements,
            Notes = notes,
            IsTodo = ContainsMarker(requirements, notes, "TODO"),
            IsVerified = null,
            SortOrder = Next(connectionSortOrders, currentRoom.Id)
        });
    }

    private void AddCheck(IReadOnlyDictionary<string, string> row)
    {
        var name = Value(row, "check");
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
        {
            SkippedCheckRows.Add($"{currentRoom!.FriendlyName}: {string.Join(" / ", row.Values)}");
            return;
        }

        var requirements = Value(row, "requirements");
        var notes = Value(row, "notes");
        checks.Add(new CheckLocation
        {
            RoomId = currentRoom!.Id,
            FriendlyName = name,
            SubroomReferenceText = EmptyToNull(Value(row, "subroom")),
            Requirements = requirements,
            Notes = notes,
            LocationType = null,
            IsTodo = ContainsMarker(requirements, notes, "TODO"),
            IsVerified = null,
            SortOrder = Next(checkSortOrders, currentRoom.Id)
        });
    }

    private Destination ParseDestination(string original)
    {
        var text = MarkdownLink.Replace(original, "$1").Trim();
        if (string.IsNullOrWhiteSpace(text)) return new Destination(string.Empty, string.Empty, false);

        var separator = text.LastIndexOf('-');
        if (separator > 0)
        {
            var roomName = text[..separator].Trim();
            var alias = text[(separator + 1)..].Trim();
            if (alias.Length is >= 1 and <= 3)
            {
                return new Destination(roomName, alias, !string.Equals(text, $"{roomName} -{alias}", StringComparison.Ordinal));
            }
        }

        var knownRoom = knownRoomNames
            .OrderByDescending(x => x.Length)
            .FirstOrDefault(x => text.StartsWith(x, StringComparison.OrdinalIgnoreCase));
        if (knownRoom is not null && !string.Equals(text, knownRoom, StringComparison.OrdinalIgnoreCase))
        {
            return new Destination(knownRoom, string.Empty, true);
        }

        return new Destination(text, string.Empty, false);
    }

    private static bool IsTableSeparator(string line) => line.StartsWith("|", StringComparison.Ordinal) && line.Contains("---", StringComparison.Ordinal);

    private static List<string> ParseCells(string line) => line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToList();

    private static string NormalizeHeader(string header) => new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Value(IReadOnlyDictionary<string, string> row, string key) => row.TryGetValue(key, out var value) ? value : string.Empty;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string AppendImportDetail(string notes, string detail) => string.IsNullOrWhiteSpace(notes) ? detail : notes + Environment.NewLine + detail;

    private static bool ContainsMarker(string first, string second, string marker) => first.Contains(marker, StringComparison.OrdinalIgnoreCase) || second.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static string ReferenceId(string value) => value.Trim().ToLowerInvariant();

    private static int Next(Dictionary<Guid, int> orders, Guid parentId)
    {
        var next = orders.GetValueOrDefault(parentId);
        orders[parentId] = next + 1;
        return next;
    }

    private void RequireGroup(int index)
    {
        if (currentGroup is null) throw new InvalidOperationException($"Room heading at line {index + 1} has no room group.");
    }

    private enum Section { None, Subrooms, Transitions, Connections, Checks, Notes }

    private sealed record Destination(string RoomName, string Alias, bool PreserveOriginal);
}

internal sealed record ImportResult(
    List<RoomGroup> RoomGroups,
    List<Room> Rooms,
    List<Subroom> Subrooms,
    List<RoomTransition> Transitions,
    List<SubroomConnection> Connections,
    List<CheckLocation> Checks);
