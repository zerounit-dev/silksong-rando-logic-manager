using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed record PublishingReviewFile(int Entry, PublishingChange Change, PublishingTextDiff? Text, string? OldBlob, string? NewHash);
public sealed class PublishingReviewRoom
{
    internal PublishingReviewRoom(int id, string name, IReadOnlyList<PublishingReviewFile> files)
    {
        (Id, Name, Files) = (id, name, files);
        // Count delta entries by their existing categories, independent of preview availability.
        foreach (var file in files)
        {
            if (file.Change.Kind == "Image") ImageCount++;
            else if (PublishingTextDiff.IsTextPath(file.Change.Path)) TextCount++;
            else OtherCount++;
        }
    }
    internal int Id { get; }
    internal string Name { get; }
    internal IReadOnlyList<PublishingReviewFile> Files { get; }
    internal int TextCount { get; }
    internal int ImageCount { get; }
    internal int OtherCount { get; }
}
internal sealed record PublishingReviewZone(string Name, IReadOnlyList<PublishingReviewRoom> Rooms);

internal sealed class PublishingRoomReview
{
    internal Guid Generation { get; } = Guid.NewGuid();
    internal IReadOnlyList<PublishingReviewZone> Zones { get; }
    internal IReadOnlyDictionary<int, PublishingReviewRoom> Rooms { get; }
    internal IReadOnlyList<PublishingReviewFile> Files { get; }
    internal PublishingReviewRoom? Publication { get; }
    internal PublishingReviewRoom? Unmapped { get; }

    internal PublishingRoomReview(List<PublishingReviewFile> files, Dictionary<int, (string Zone, string Slug)> locations)
    {
        Files = files.AsReadOnly();
        var rooms = new List<PublishingReviewRoom>();
        PublishingReviewRoom Room(string name, IEnumerable<PublishingReviewFile> entries)
        {
            var room = new PublishingReviewRoom(rooms.Count, name, entries.OrderBy(x => x.Change.Kind == "Image").ToArray());
            rooms.Add(room);
            return room;
        }
        Zones = files.Where(x => locations.ContainsKey(x.Entry)).GroupBy(x => locations[x.Entry].Zone)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(zone => new PublishingReviewZone(zone.Key, zone.GroupBy(x => locations[x.Entry].Slug)
                .OrderBy(x => x.Key, StringComparer.Ordinal).Select(room => Room(room.Key, room)).ToArray())).ToArray();
        var other = files.Where(x => !locations.ContainsKey(x.Entry)).ToLookup(x => IsPublicationWide(x.Change.Path));
        if (other[true].Any()) Publication = Room("Publication-wide artifacts", other[true]);
        if (other[false].Any()) Unmapped = Room("Unmapped / legacy artifacts", other[false]);
        Rooms = rooms.ToDictionary(x => x.Id);
    }

    private static bool IsPublicationWide(string path) => path is "README.md" or "MASTER.md" or "silksong-rando-logic.db"
        or "silksong-room-catalogue.json" or "silksong-requirement-catalogue.json" or "room_graph_data.json" or "room_geometry_data.json";
    private static bool Slug(string s) => s.Length != 0 && s[0] != '-' && s[^1] != '-' && !s.Contains("--", StringComparison.Ordinal)
        && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    internal static (string Zone, string Slug)? RoomPath(string path)
    {
        var parts = path.Split('/');
        if (parts is [var zone, var page] && zone is not ("00-annotations" or "01-geometry") && Slug(zone)
            && page.EndsWith(".md", StringComparison.Ordinal) && Slug(page[..^3])) return (zone, page[..^3]);
        if (parts is ["00-annotations", var area, var asset] && Slug(area))
            foreach (var suffix in new[] { "-scene.png", "-connections.png", "-checks.png" })
                if (asset.EndsWith(suffix, StringComparison.Ordinal) && Slug(asset[..^suffix.Length])) return (area, asset[..^suffix.Length]);
        return null;
    }

    internal static Guid? GeometryId(string path) => path.StartsWith("01-geometry/", StringComparison.Ordinal)
        && path.EndsWith(".webp", StringComparison.Ordinal) && Guid.TryParseExact(path[12..^5], "D", out var id) ? id : null;

    // Match only a unique, same-side generated index link and geometry witness.
    // Don't infer suffix/collision assignments from names or map a deletion using the candidate.
    internal static Dictionary<Guid, (string Zone, string Slug)> GeometryRooms(byte[]? geometry, byte[]? index, Func<string, bool> hasPage)
    {
        if (geometry is null || index is null) return [];
        var matches = new Dictionary<(string Group, string Title), List<(string Zone, string Slug)>>();
        var markdown = Encoding.UTF8.GetString(index);
        if (!markdown.StartsWith("# Silksong Randomizer Notes\n\nGenerated from the non-archived records in the accompanying frozen SQLite snapshot.", StringComparison.Ordinal)) return [];
        var group = "";
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) { group = line[3..]; continue; }
            if (!line.StartsWith("- [", StringComparison.Ordinal) || !line.EndsWith(')')) continue;
            var separator = line.LastIndexOf("](./", StringComparison.Ordinal);
            if (separator < 3) continue;
            var path = line[(separator + 4)..^1];
            if (RoomPath(path) is not { } room || !path.EndsWith(".md", StringComparison.Ordinal) || !hasPage(path)) continue;
            var key = (group, line[3..separator]);
            if (!matches.TryGetValue(key, out var locations)) matches.Add(key, locations = []);
            locations.Add(room);
        }
        try
        {
            using var document = JsonDocument.Parse(geometry);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("schema_version", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 2
                || !document.RootElement.TryGetProperty("rooms", out var rows)
                || rows.ValueKind != JsonValueKind.Array) return [];
            var witnesses = new List<(Guid Id, (string Group, string Title) Key)>();
            foreach (var row in rows.EnumerateArray())
            {
                var name = String(row, "name"); var zone = String(row, "room_group_name"); var game = String(row, "in_game_id");
                if (name is null || zone is null || !Guid.TryParseExact(String(row, "image_guid"), "D", out var id)) continue;
                witnesses.Add((id, (zone, Regex.Replace(name + (string.IsNullOrEmpty(game) ? "" : $" ({game})"), @"\s+", " ").Trim())));
            }
            return witnesses.GroupBy(x => x.Key).Where(x => x.Count() == 1 && matches.TryGetValue(x.Key, out var paths) && paths.Count == 1)
                .Select(x => (x.Single().Id, Path: matches[x.Key][0])).GroupBy(x => x.Id).Where(x => x.Count() == 1)
                .ToDictionary(x => x.Key, x => x.Single().Path);
        }
        catch (JsonException) { return []; } // Legacy export has no usable witness.

        static string? String(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

}
