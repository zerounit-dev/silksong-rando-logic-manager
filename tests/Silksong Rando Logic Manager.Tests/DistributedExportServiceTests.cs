using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class DistributedExportServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-distributed-export-{Guid.NewGuid():N}.db");
    private static readonly DateTime Stamp = new(2026, 8, 16, 14, 30, 45, 123, DateTimeKind.Utc);
    private static readonly DateTime Date = new(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CompleteExport_MigrationCurrentSqlite_PreservesAllFieldsNullsArchivesOrderingScopesAndMapShape()
    {
        var ids = await SeedCatalogueAsync();
        var export = await Service().ExportCompleteAsync(true);
        using var json = JsonDocument.Parse(export.JsonUtf8);
        var root = json.RootElement;

        Assert.Equal("silksong-logic-20260816-143045-123.json", export.FileName);
        AssertObject(root, ["exportVersion", "isPartialRoomDump", "rooms", "roomGroupings", "areaMap"],
            ("exportVersion", 2), ("isPartialRoomDump", false));

        var rooms = root.GetProperty("rooms").EnumerateArray().ToArray();
        Assert.Equal([ids.ArchivedRoom, ids.ActiveRoom, ids.EmptyRoom], rooms.Select(x => x.GetProperty("id").GetGuid()));
        AssertArchivedPopulatedRoom(rooms[0], ids.GroupA);
        AssertActiveNullRoom(rooms[1], ids.GroupB);
        AssertEmptyRoom(rooms[2]);

        var groups = root.GetProperty("roomGroupings").GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal([ids.GroupB, ids.GroupA], groups.Select(x => x.GetProperty("id").GetGuid()));
        AssertGroup(groups[0], ids.GroupB, "Group B", null, -4, Date);
        AssertGroup(groups[1], ids.GroupA, "Group A", "zone-a", 9, Date);

        var maps = root.GetProperty("areaMap").GetProperty("maps").EnumerateArray().ToArray();
        Assert.Equal([ids.MapNull, ids.MapPopulated], maps.Select(x => x.GetProperty("id").GetGuid()));
        AssertNullMap(maps[0]);
        AssertPopulatedMap(maps[1]);
        AssertExcludedMetadata(export.JsonUtf8);
    }

    [Fact]
    public async Task CurrentExport_MigrationCurrentSqlite_HasExactRoomAndReferencedGroupOnly_NoMap_AndEveryFilenameCase()
    {
        var ids = await SeedCatalogueAsync();

        var current = Assert.IsType<CurrentRoomExported>(await Service().ExportCurrentRoomAsync(ids.ActiveRoom)).Export;
        using (var json = JsonDocument.Parse(current.JsonUtf8))
        {
            var root = json.RootElement;
            Assert.Equal("active-ref-20260816-143045-123.json", current.FileName);
            AssertObject(root, ["exportVersion", "isPartialRoomDump", "rooms", "roomGroupings"],
                ("exportVersion", 2), ("isPartialRoomDump", true));
            Assert.False(root.TryGetProperty("areaMap", out _));
            var room = Assert.Single(root.GetProperty("rooms").EnumerateArray());
            Assert.Equal(ids.ActiveRoom, room.GetProperty("id").GetGuid());
            AssertActiveNullRoom(room, ids.GroupB);
            var group = Assert.Single(root.GetProperty("roomGroupings").GetProperty("groups").EnumerateArray());
            AssertGroup(group, ids.GroupB, "Group B", null, -4, Date);
            AssertExcludedMetadata(current.JsonUtf8);
        }

        var empty = Assert.IsType<CurrentRoomExported>(await Service().ExportCurrentRoomAsync(ids.EmptyRoom)).Export;
        using (var json = JsonDocument.Parse(empty.JsonUtf8))
        {
            Assert.Equal("empty-room-20260816-143045-123.json", empty.FileName);
            var root = json.RootElement;
            Assert.True(root.GetProperty("isPartialRoomDump").GetBoolean());
            Assert.False(root.TryGetProperty("areaMap", out _));
            AssertEmptyRoom(Assert.Single(root.GetProperty("rooms").EnumerateArray()));
            Assert.Empty(root.GetProperty("roomGroupings").GetProperty("groups").EnumerateArray());
        }

        var completeWithoutMap = await Service().ExportCompleteAsync(false);
        using var completeJson = JsonDocument.Parse(completeWithoutMap.JsonUtf8);
        Assert.False(completeJson.RootElement.TryGetProperty("areaMap", out _));
        Assert.IsType<CurrentRoomMissing>(await Service().ExportCurrentRoomAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Exports_MigrationCurrentSqlite_UnspecifiedAuditTimestampsRemainSameTicksWithZAndPassStrictParser()
    {
        var ids = await SeedCatalogueAsync();
        var complete = await Service().ExportCompleteAsync(true);
        var current = Assert.IsType<CurrentRoomExported>(await Service().ExportCurrentRoomAsync(ids.ArchivedRoom)).Export;
        var parser = new DistributedImportPackageParser();

        Assert.Equal(DateTimeKind.Unspecified, complete.Envelope.RoomGroupings.Groups[0].CreatedUtc.Kind);
        Assert.Equal(DateTimeKind.Unspecified, complete.Envelope.Rooms[0].CreatedUtc.Kind);
        Assert.Equal(DateTimeKind.Unspecified, complete.Envelope.Rooms[0].ArchivedUtc!.Value.Kind);
        Assert.All(complete.Envelope.Rooms[0].Subrooms, row => Assert.Equal(DateTimeKind.Unspecified, row.CreatedUtc.Kind));
        Assert.All(complete.Envelope.Rooms[0].Transitions, row => Assert.Equal(DateTimeKind.Unspecified, row.CreatedUtc.Kind));
        Assert.All(complete.Envelope.Rooms[0].Connections, row => Assert.Equal(DateTimeKind.Unspecified, row.CreatedUtc.Kind));
        Assert.All(complete.Envelope.Rooms[0].Checks, row => Assert.Equal(DateTimeKind.Unspecified, row.CreatedUtc.Kind));
        Assert.IsType<DistributedImportPackageValidated>(parser.Parse(complete.JsonUtf8));
        Assert.IsType<DistributedImportPackageValidated>(parser.Parse(current.JsonUtf8));
        AssertAllAuditTimestampStringsAreUtcWithUnchangedTicks(complete.JsonUtf8, expectedCount: 39);
        AssertAllAuditTimestampStringsAreUtcWithUnchangedTicks(current.JsonUtf8, expectedCount: 25);
    }

    [Fact]
    public async Task CompleteExport_MigrationCurrentSqlite_UnseededCatalogue_OmitsOrIncludesAuthoritativeEmptyAreaMap()
    {
        var withoutMap = await Service().ExportCompleteAsync(false);
        using (var json = JsonDocument.Parse(withoutMap.JsonUtf8))
        {
            var root = json.RootElement;
            AssertObject(root, ["exportVersion", "isPartialRoomDump", "rooms", "roomGroupings"], ("exportVersion", 2), ("isPartialRoomDump", false));
            Assert.Empty(root.GetProperty("rooms").EnumerateArray());
            Assert.Empty(root.GetProperty("roomGroupings").GetProperty("groups").EnumerateArray());
            Assert.False(root.TryGetProperty("areaMap", out _));
        }

        var withMap = await Service().ExportCompleteAsync(true);
        using var mapJson = JsonDocument.Parse(withMap.JsonUtf8);
        var mapRoot = mapJson.RootElement;
        AssertObject(mapRoot, ["exportVersion", "isPartialRoomDump", "rooms", "roomGroupings", "areaMap"], ("exportVersion", 2), ("isPartialRoomDump", false));
        Assert.Empty(mapRoot.GetProperty("rooms").EnumerateArray());
        Assert.Empty(mapRoot.GetProperty("roomGroupings").GetProperty("groups").EnumerateArray());
        Assert.Empty(mapRoot.GetProperty("areaMap").GetProperty("maps").EnumerateArray());
    }

    [Fact]
    public void ExportContractGraph_RecursivelyRejectsEfEntitiesAndDbContexts_IncludingNestedGenericFixtures()
    {
        var contracts = typeof(DistributedExportResult).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(DistributedExportResult).Namespace &&
                           (type.Name.StartsWith("Distributed", StringComparison.Ordinal) || type.Name.StartsWith("Version2", StringComparison.Ordinal) || type.Name.StartsWith("CurrentRoom", StringComparison.Ordinal)));
        foreach (var contract in contracts)
            Assert.Empty(FindProhibitedContractTypes(contract));

        var nested = FindProhibitedContractTypes(typeof(NestedEntityFixture));
        Assert.Contains(nested, x => x.Contains("Room", StringComparison.Ordinal));
        var generic = FindProhibitedContractTypes(typeof(GenericContextFixture));
        Assert.Contains(generic, x => x.Contains("LogicDbContext", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> FindProhibitedContractTypes(Type root)
    {
        var prohibitedEntities = new[] { typeof(Room), typeof(RoomGroup), typeof(Subroom), typeof(RoomTransition), typeof(SubroomConnection), typeof(CheckLocation), typeof(Map), typeof(MapOverlay), typeof(MapZone), typeof(MapScene), typeof(MapChunk) };
        var violations = new List<string>();
        Visit(root, root.Name, new HashSet<Type>());
        return violations;

        void Visit(Type type, string path, HashSet<Type> branch)
        {
            if (typeof(DbContext).IsAssignableFrom(type) || prohibitedEntities.Any(entity => entity.IsAssignableFrom(type)))
            {
                violations.Add($"{path}: {type.FullName}");
                return;
            }

            if (type.IsArray) { Visit(type.GetElementType()!, $"{path}[]", branch); return; }
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments()) Visit(argument, $"{path}<{argument.Name}>", branch);
            if (!type.IsClass || type == typeof(string) || !branch.Add(type)) return;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Visit(property.PropertyType, $"{path}.{property.Name}", new HashSet<Type>(branch));
        }
    }

    private static void AssertArchivedPopulatedRoom(JsonElement room, Guid groupId)
    {
        AssertObject(room, ["id", "roomGroupId", "referenceId", "friendlyName", "inGameId", "contributors", "comments", "sceneUnitWidth", "sceneUnitHeight", "sceneImageScaleXPercent", "sceneImageScaleYPercent", "sceneImagePanXPercent", "sceneImagePanYPercent", "isSceneImageStale", "sortOrder", "isArchived", "archivedUtc", "createdUtc", "updatedUtc", "subrooms", "transitions", "connections", "checks"],
            ("id", Id(3)), ("roomGroupId", groupId), ("referenceId", "archived-ref"), ("friendlyName", "Archived room"), ("inGameId", "game-room"), ("contributors", "author"), ("comments", "**comments**"), ("sceneUnitWidth", 10d), ("sceneUnitHeight", 20d), ("sceneImageScaleXPercent", 30d), ("sceneImageScaleYPercent", 40d), ("sceneImagePanXPercent", 50d), ("sceneImagePanYPercent", 60d), ("isSceneImageStale", true), ("sortOrder", -8), ("isArchived", true), ("archivedUtc", Date), ("createdUtc", Date), ("updatedUtc", Date));
        var subrooms = room.GetProperty("subrooms").EnumerateArray().ToArray();
        Assert.Equal(2, subrooms.Length);
        AssertSubroom(subrooms[0], Id(101), "sub-active", "Sub active", "sub note", 1, 2, 3, 4, true, -2, false, null);
        AssertSubroom(subrooms[1], Id(102), "sub-archived", "Sub archived", "old sub", 5, 6, 7, 8, false, 3, true, Date);
        var transitions = room.GetProperty("transitions").EnumerateArray().ToArray();
        Assert.Equal(2, transitions.Length);
        AssertTransition(transitions[0], Id(201), "transition-active", true, false);
        AssertTransition(transitions[1], Id(202), "transition-archived", true, true);
        var connections = room.GetProperty("connections").EnumerateArray().ToArray();
        Assert.Equal(2, connections.Length);
        AssertConnection(connections[0], Id(301), "connection-active", true, false);
        AssertConnection(connections[1], Id(302), "connection-archived", true, true);
        var checks = room.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(2, checks.Length);
        AssertCheck(checks[0], Id(401), "Check active", true, false);
        AssertCheck(checks[1], Id(402), "Check archived", true, true);
    }

    private static void AssertSubroom(JsonElement row, Guid id, string referenceId, string friendlyName, string? notes, double? x, double? y, double? width, double? height, bool enableAnnotation, int sortOrder, bool archived, DateTime? archivedUtc)
    {
        AssertObject(row, ["id", "referenceId", "friendlyName", "notes", "sceneUnitX", "sceneUnitY", "sceneUnitWidth", "sceneUnitHeight", "enableAnnotation", "sortOrder", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"], ("id", id), ("referenceId", referenceId), ("friendlyName", friendlyName), ("notes", notes), ("sceneUnitX", x), ("sceneUnitY", y), ("sceneUnitWidth", width), ("sceneUnitHeight", height), ("enableAnnotation", enableAnnotation), ("sortOrder", sortOrder), ("isArchived", archived), ("archivedUtc", archivedUtc), ("createdUtc", Date), ("updatedUtc", Date));
    }

    private static void AssertTransition(JsonElement row, Guid id, string alias, bool populated, bool archived)
        => AssertObject(row, ["id", "alias", "friendlyName", "inGameId", "inGamePositionX", "inGamePositionY", "inGamePositionZ", "localPositionX", "localPositionY", "localPositionZ", "annotationSceneUnitX", "annotationSceneUnitY", "enableAnnotation", "sourceSubroomReferenceText", "destinationRoomReferenceText", "destinationTransitionAliasText", "requirements", "notes", "sortOrder", "isTodo", "isVerified", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"],
            ("id", id), ("alias", alias), ("friendlyName", "Transition"), ("inGameId", populated ? "tid" : null), ("inGamePositionX", populated ? 1d : null), ("inGamePositionY", populated ? 2d : null), ("inGamePositionZ", populated ? 3d : null), ("localPositionX", populated ? 4d : null), ("localPositionY", populated ? 5d : null), ("localPositionZ", populated ? 6d : null), ("annotationSceneUnitX", populated ? 7d : null), ("annotationSceneUnitY", populated ? 8d : null), ("enableAnnotation", populated), ("sourceSubroomReferenceText", populated ? "sub-active" : null), ("destinationRoomReferenceText", populated ? "dest" : null), ("destinationTransitionAliasText", populated ? "d" : null), ("requirements", "req"), ("notes", "note"), ("sortOrder", archived ? 3 : 0), ("isTodo", populated), ("isVerified", populated ? !archived : null), ("isArchived", archived), ("archivedUtc", archived ? Date : null), ("createdUtc", Date), ("updatedUtc", Date));

    private static void AssertConnection(JsonElement row, Guid id, string alias, bool populated, bool archived)
        => AssertObject(row, ["id", "alias", "friendlyName", "sourceSubroomReferenceText", "destinationSubroomReferenceText", "requirements", "notes", "enableAnnotation", "sceneUnitX", "sceneUnitY", "sortOrder", "isTodo", "isVerified", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"],
            ("id", id), ("alias", alias), ("friendlyName", "Connection"), ("sourceSubroomReferenceText", "sub-active"), ("destinationSubroomReferenceText", "sub-archived"), ("requirements", "req"), ("notes", "note"), ("enableAnnotation", populated), ("sceneUnitX", populated ? 1d : null), ("sceneUnitY", populated ? 2d : null), ("sortOrder", archived ? 3 : 0), ("isTodo", populated), ("isVerified", populated ? !archived : null), ("isArchived", archived), ("archivedUtc", archived ? Date : null), ("createdUtc", Date), ("updatedUtc", Date));

    private static void AssertCheck(JsonElement row, Guid id, string friendlyName, bool populated, bool archived)
        => AssertObject(row, ["id", "friendlyName", "inGameId", "inGamePositionX", "inGamePositionY", "inGamePositionZ", "localPositionX", "localPositionY", "localPositionZ", "annotationSceneUnitX", "annotationSceneUnitY", "subroomReferenceText", "requirements", "notes", "isIncludedInApworld", "enableAnnotation", "sortOrder", "isTodo", "isVerified", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"],
            ("id", id), ("friendlyName", friendlyName), ("inGameId", populated ? "cid" : null), ("inGamePositionX", populated ? 1d : null), ("inGamePositionY", populated ? 2d : null), ("inGamePositionZ", populated ? 3d : null), ("localPositionX", populated ? 4d : null), ("localPositionY", populated ? 5d : null), ("localPositionZ", populated ? 6d : null), ("annotationSceneUnitX", populated ? 7d : null), ("annotationSceneUnitY", populated ? 8d : null), ("subroomReferenceText", populated ? "sub-active" : null), ("requirements", "req"), ("notes", "note"), ("isIncludedInApworld", !populated), ("enableAnnotation", populated), ("sortOrder", archived ? 3 : 0), ("isTodo", populated), ("isVerified", populated ? !archived : null), ("isArchived", archived), ("archivedUtc", archived ? Date : null), ("createdUtc", Date), ("updatedUtc", Date));

    private static void AssertActiveNullRoom(JsonElement room, Guid groupId)
    {
        AssertObject(room, ["id", "roomGroupId", "referenceId", "friendlyName", "inGameId", "contributors", "comments", "sceneUnitWidth", "sceneUnitHeight", "sceneImageScaleXPercent", "sceneImageScaleYPercent", "sceneImagePanXPercent", "sceneImagePanYPercent", "isSceneImageStale", "sortOrder", "isArchived", "archivedUtc", "createdUtc", "updatedUtc", "subrooms", "transitions", "connections", "checks"],
            ("id", Id(4)), ("roomGroupId", groupId), ("referenceId", "active-ref"), ("friendlyName", "Active room"), ("inGameId", null), ("contributors", null), ("comments", null), ("sceneUnitWidth", null), ("sceneUnitHeight", null), ("sceneImageScaleXPercent", null), ("sceneImageScaleYPercent", null), ("sceneImagePanXPercent", null), ("sceneImagePanYPercent", null), ("isSceneImageStale", false), ("sortOrder", 2), ("isArchived", false), ("archivedUtc", null), ("createdUtc", Date), ("updatedUtc", Date));
        AssertSubroom(Assert.Single(room.GetProperty("subrooms").EnumerateArray()), Id(103), "null-sub", "Null sub", null, null, null, null, null, false, 0, false, null);
        AssertTransition(Assert.Single(room.GetProperty("transitions").EnumerateArray()), Id(203), "transition-null", false, false);
        AssertConnection(Assert.Single(room.GetProperty("connections").EnumerateArray()), Id(303), "connection-null", false, false);
        AssertCheck(Assert.Single(room.GetProperty("checks").EnumerateArray()), Id(403), "Check null", false, false);
    }

    private static void AssertEmptyRoom(JsonElement room)
    {
        AssertObject(room, ["id", "roomGroupId", "referenceId", "friendlyName", "inGameId", "contributors", "comments", "sceneUnitWidth", "sceneUnitHeight", "sceneImageScaleXPercent", "sceneImageScaleYPercent", "sceneImagePanXPercent", "sceneImagePanYPercent", "isSceneImageStale", "sortOrder", "isArchived", "archivedUtc", "createdUtc", "updatedUtc", "subrooms", "transitions", "connections", "checks"],
            ("id", Id(5)), ("roomGroupId", null), ("referenceId", ""), ("friendlyName", "Empty room"), ("inGameId", null), ("contributors", null), ("comments", null), ("sceneUnitWidth", null), ("sceneUnitHeight", null), ("sceneImageScaleXPercent", null), ("sceneImageScaleYPercent", null), ("sceneImagePanXPercent", null), ("sceneImagePanYPercent", null), ("isSceneImageStale", false), ("sortOrder", 7), ("isArchived", false), ("archivedUtc", null), ("createdUtc", Date), ("updatedUtc", Date));
        foreach (var child in new[] { "subrooms", "transitions", "connections", "checks" }) Assert.Empty(room.GetProperty(child).EnumerateArray());
    }

    private static void AssertGroup(JsonElement group, Guid id, string name, string? zone, int order, DateTime timestamp)
    {
        AssertObject(group, ["id", "friendlyName", "zoneReferenceText", "sortOrder", "createdUtc", "updatedUtc"], ("id", id), ("friendlyName", name), ("zoneReferenceText", zone), ("sortOrder", order), ("createdUtc", timestamp), ("updatedUtc", timestamp));
    }

    private static void AssertPopulatedMap(JsonElement map)
    {
        AssertObject(map, ["id", "inGameId", "friendlyName", "sortOrder", "mapUnitMinX", "mapUnitMinY", "mapUnitMaxX", "mapUnitMaxY", "overlays", "zones"], ("id", Id(501)), ("inGameId", "map-populated"), ("friendlyName", "Map"), ("sortOrder", 5), ("mapUnitMinX", 1d), ("mapUnitMinY", 2d), ("mapUnitMaxX", 3d), ("mapUnitMaxY", 4d));
        var overlays = map.GetProperty("overlays").EnumerateArray().ToArray();
        Assert.Equal(2, overlays.Length);
        AssertObject(overlays[0], ["id", "friendlyName", "imageAssetKey", "scaleXPercent", "scaleYPercent", "leftOffsetPercent", "bottomOffsetPercent", "sortOrder"], ("id", Id(601)), ("friendlyName", "Overlay first"), ("imageAssetKey", "area-map-hd"), ("scaleXPercent", 11d), ("scaleYPercent", 12d), ("leftOffsetPercent", 13d), ("bottomOffsetPercent", 14d), ("sortOrder", -1));
        AssertObject(overlays[1], ["id", "friendlyName", "imageAssetKey", "scaleXPercent", "scaleYPercent", "leftOffsetPercent", "bottomOffsetPercent", "sortOrder"], ("id", Id(602)), ("friendlyName", "Overlay second"), ("imageAssetKey", "second"), ("scaleXPercent", 21d), ("scaleYPercent", 22d), ("leftOffsetPercent", 23d), ("bottomOffsetPercent", 24d), ("sortOrder", 3));
        var zones = map.GetProperty("zones").EnumerateArray().ToArray();
        Assert.Equal(2, zones.Length);
        AssertObject(zones[0], ["id", "inGameId", "friendlyName", "mapUnitMinX", "mapUnitMinY", "mapUnitMaxX", "mapUnitMaxY", "scenes"], ("id", Id(701)), ("inGameId", "a-zone"), ("friendlyName", null), ("mapUnitMinX", null), ("mapUnitMinY", null), ("mapUnitMaxX", null), ("mapUnitMaxY", null));
        Assert.Empty(zones[0].GetProperty("scenes").EnumerateArray());
        var scenes = zones[1].GetProperty("scenes").EnumerateArray().ToArray();
        AssertObject(zones[1], ["id", "inGameId", "friendlyName", "mapUnitMinX", "mapUnitMinY", "mapUnitMaxX", "mapUnitMaxY", "scenes"], ("id", Id(702)), ("inGameId", "z-zone"), ("friendlyName", "Zone"), ("mapUnitMinX", 1d), ("mapUnitMinY", 2d), ("mapUnitMaxX", 3d), ("mapUnitMaxY", 4d));
        Assert.Equal(2, scenes.Length);
        AssertObject(scenes[0], ["id", "inGameId", "friendlyName", "roomReferenceText", "chunks"], ("id", Id(801)), ("inGameId", "a-scene"), ("friendlyName", null), ("roomReferenceText", null));
        Assert.Empty(scenes[0].GetProperty("chunks").EnumerateArray());
        AssertObject(scenes[1], ["id", "inGameId", "friendlyName", "roomReferenceText", "chunks"], ("id", Id(802)), ("inGameId", "z-scene"), ("friendlyName", "Scene"), ("roomReferenceText", "archived-ref"));
        var chunks = scenes[1].GetProperty("chunks").EnumerateArray().ToArray();
        Assert.Equal(2, chunks.Length);
        AssertObject(chunks[0], ["id", "cacheIndex", "initialState", "mapUnitMinX", "mapUnitMinY", "mapUnitMaxX", "mapUnitMaxY", "mapUnitZ"], ("id", Id(901)), ("cacheIndex", 1), ("initialState", null), ("mapUnitMinX", null), ("mapUnitMinY", null), ("mapUnitMaxX", null), ("mapUnitMaxY", null), ("mapUnitZ", null));
        AssertObject(chunks[1], ["id", "cacheIndex", "initialState", "mapUnitMinX", "mapUnitMinY", "mapUnitMaxX", "mapUnitMaxY", "mapUnitZ"], ("id", Id(902)), ("cacheIndex", 9), ("initialState", "Shown"), ("mapUnitMinX", 1d), ("mapUnitMinY", 2d), ("mapUnitMaxX", 3d), ("mapUnitMaxY", 4d), ("mapUnitZ", 5d));
    }

    private static void AssertNullMap(JsonElement map)
    {
        AssertObject(map, ["id", "inGameId", "friendlyName", "sortOrder", "mapUnitMinX", "mapUnitMinY", "mapUnitMaxX", "mapUnitMaxY", "overlays", "zones"], ("id", Id(502)), ("inGameId", "map-null"), ("friendlyName", null), ("sortOrder", -3), ("mapUnitMinX", null), ("mapUnitMinY", null), ("mapUnitMaxX", null), ("mapUnitMaxY", null));
        Assert.Empty(map.GetProperty("overlays").EnumerateArray());
        Assert.Empty(map.GetProperty("zones").EnumerateArray());
    }

    private static void AssertObject(JsonElement element, string[] fields, params (string Name, object? Value)[] values)
    {
        AssertFieldNames(element, fields);
        foreach (var (name, value) in values) AssertValue(element.GetProperty(name), value, name);
    }

    private static void AssertFieldNames(JsonElement element, IEnumerable<string> fields) => Assert.Equal(fields.OrderBy(x => x), element.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    private static void AssertValue(JsonElement value, object? expected, string name)
    {
        if (expected is null) { Assert.Equal(JsonValueKind.Null, value.ValueKind); return; }
        switch (expected)
        {
            case Guid guid: Assert.Equal(guid, value.GetGuid()); break;
            case DateTime date: Assert.Equal(date, value.GetDateTime()); break;
            case string text: Assert.Equal(text, value.GetString()); break;
            case int integer: Assert.Equal(integer, value.GetInt32()); break;
            case double number: Assert.Equal(number, value.GetDouble()); break;
            case bool flag: Assert.Equal(flag, value.GetBoolean()); break;
            default: throw new ArgumentOutOfRangeException(nameof(expected), expected, name);
        }
    }

    private static void AssertExcludedMetadata(byte[] json)
    {
        var text = System.Text.Encoding.UTF8.GetString(json);
        const string excluded = "resolvedSourceSubroomId|resolvedDestinationRoomId|resolvedDestinationTransitionId|resolvedDestinationSubroomId|resolvedSubroomId|resolvedMapZoneId|resolvedRoomId|roomId|mapId|mapZoneId|mapSceneId";
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(excluded, System.Text.RegularExpressions.RegexOptions.IgnoreCase), text);
        Assert.DoesNotContain(".webp", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertAllAuditTimestampStringsAreUtcWithUnchangedTicks(byte[] json, int expectedCount)
    {
        using var document = JsonDocument.Parse(json);
        var timestamps = new List<string>();
        CollectAuditTimestampStrings(document.RootElement, timestamps);
        Assert.Equal(expectedCount, timestamps.Count);
        foreach (var timestamp in timestamps)
        {
            Assert.EndsWith("Z", timestamp, StringComparison.Ordinal);
            Assert.True(DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed));
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
            Assert.Equal(Date.Ticks, parsed.Ticks);
        }
    }

    private static void CollectAuditTimestampStrings(JsonElement value, List<string> timestamps)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name is "createdUtc" or "updatedUtc" ||
                        (property.Name == "archivedUtc" && property.Value.ValueKind != JsonValueKind.Null))
                        timestamps.Add(property.Value.GetString()!);
                    else
                        CollectAuditTimestampStrings(property.Value, timestamps);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CollectAuditTimestampStrings(item, timestamps);
                break;
        }
    }

    private DistributedExportService Service() => new(new Factory(databasePath), new FixedTimeProvider(Stamp));
    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private async Task<(Guid GroupA, Guid GroupB, Guid ArchivedRoom, Guid ActiveRoom, Guid EmptyRoom, Guid MapPopulated, Guid MapNull)> SeedCatalogueAsync()
    {
        await using var db = CreateContext();
        var groupA = new RoomGroup { Id = Id(1), FriendlyName = "Group A", ZoneReferenceText = "zone-a", SortOrder = 9, CreatedUtc = Date, UpdatedUtc = Date };
        var groupB = new RoomGroup { Id = Id(2), FriendlyName = "Group B", ZoneReferenceText = null, SortOrder = -4, CreatedUtc = Date.AddDays(1), UpdatedUtc = Date.AddDays(1) };
        var archived = new Room { Id = Id(3), RoomGroupId = groupA.Id, ReferenceId = "archived-ref", FriendlyName = "Archived room", InGameId = "game-room", Contributors = "author", Comments = "**comments**", SceneUnitWidth = 10, SceneUnitHeight = 20, SceneImageScaleXPercent = 30, SceneImageScaleYPercent = 40, SceneImagePanXPercent = 50, SceneImagePanYPercent = 60, IsSceneImageStale = true, SortOrder = -8, IsArchived = true, ArchivedUtc = Date, CreatedUtc = Date, UpdatedUtc = Date };
        var active = new Room { Id = Id(4), RoomGroupId = groupB.Id, ReferenceId = "active-ref", FriendlyName = "Active room", Comments = null, SortOrder = 2, CreatedUtc = Date.AddDays(1), UpdatedUtc = Date.AddDays(1) };
        var empty = new Room { Id = Id(5), ReferenceId = "", FriendlyName = "Empty room", Comments = null, SortOrder = 7, CreatedUtc = Date.AddDays(2), UpdatedUtc = Date.AddDays(2) };
        db.AddRange(groupA, groupB, archived, active, empty);
        await db.SaveChangesAsync();
        db.AddRange(
            new Subroom { Id = Id(101), RoomId = archived.Id, ReferenceId = "sub-active", FriendlyName = "Sub active", Notes = "sub note", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4, EnableAnnotation = true, SortOrder = -2, CreatedUtc = Date, UpdatedUtc = Date },
            new Subroom { Id = Id(102), RoomId = archived.Id, ReferenceId = "sub-archived", FriendlyName = "Sub archived", Notes = "old sub", SceneUnitX = 5, SceneUnitY = 6, SceneUnitWidth = 7, SceneUnitHeight = 8, EnableAnnotation = false, SortOrder = 3, IsArchived = true, ArchivedUtc = Date, CreatedUtc = Date, UpdatedUtc = Date },
            new Subroom { Id = Id(103), RoomId = active.Id, ReferenceId = "null-sub", FriendlyName = "Null sub", Notes = null, EnableAnnotation = false, SortOrder = 0, CreatedUtc = Date.AddDays(1), UpdatedUtc = Date.AddDays(1) });
        AddChildren(db, archived.Id, false, Date, 201, 301, 401);
        AddChildren(db, archived.Id, true, Date, 202, 302, 402);
        AddChildren(db, active.Id, null, Date.AddDays(1), 203, 303, 403);
        await db.SaveChangesAsync();
        var mapPopulated = new Map { Id = Id(501), InGameId = "map-populated", FriendlyName = "Map", SortOrder = 5, MapUnitMinX = 1, MapUnitMinY = 2, MapUnitMaxX = 3, MapUnitMaxY = 4 };
        var mapNull = new Map { Id = Id(502), InGameId = "map-null", SortOrder = -3 };
        db.AddRange(mapPopulated, mapNull);
        await db.SaveChangesAsync();
        var aZone = new MapZone { Id = Id(701), MapId = mapPopulated.Id, InGameId = "a-zone" };
        var zZone = new MapZone { Id = Id(702), MapId = mapPopulated.Id, InGameId = "z-zone", FriendlyName = "Zone", MapUnitMinX = 1, MapUnitMinY = 2, MapUnitMaxX = 3, MapUnitMaxY = 4 };
        db.AddRange(aZone, zZone, new MapOverlay { Id = Id(602), MapId = mapPopulated.Id, FriendlyName = "Overlay second", ImageAssetKey = "second", ScaleXPercent = 21, ScaleYPercent = 22, LeftOffsetPercent = 23, BottomOffsetPercent = 24, SortOrder = 3 }, new MapOverlay { Id = Id(601), MapId = mapPopulated.Id, FriendlyName = "Overlay first", ImageAssetKey = "area-map-hd", ScaleXPercent = 11, ScaleYPercent = 12, LeftOffsetPercent = 13, BottomOffsetPercent = 14, SortOrder = -1 });
        await db.SaveChangesAsync();
        var aScene = new MapScene { Id = Id(801), MapZoneId = zZone.Id, InGameId = "a-scene" };
        var zScene = new MapScene { Id = Id(802), MapZoneId = zZone.Id, InGameId = "z-scene", FriendlyName = "Scene", RoomReferenceText = "archived-ref", ResolvedRoomId = archived.Id };
        db.AddRange(aScene, zScene);
        await db.SaveChangesAsync();
        db.AddRange(new MapChunk { Id = Id(902), MapSceneId = zScene.Id, CacheIndex = 9, InitialState = "Shown", MapUnitMinX = 1, MapUnitMinY = 2, MapUnitMaxX = 3, MapUnitMaxY = 4, MapUnitZ = 5 }, new MapChunk { Id = Id(901), MapSceneId = zScene.Id, CacheIndex = 1 });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync($"UPDATE \"RoomGroups\" SET \"CreatedUtc\" = {Date}, \"UpdatedUtc\" = {Date}");
        await db.Database.ExecuteSqlAsync($"UPDATE \"Rooms\" SET \"CreatedUtc\" = {Date}, \"UpdatedUtc\" = {Date}, \"ArchivedUtc\" = CASE WHEN \"IsArchived\" THEN {Date} ELSE NULL END");
        await db.Database.ExecuteSqlAsync($"UPDATE \"Subrooms\" SET \"CreatedUtc\" = {Date}, \"UpdatedUtc\" = {Date}, \"ArchivedUtc\" = CASE WHEN \"IsArchived\" THEN {Date} ELSE NULL END");
        await db.Database.ExecuteSqlAsync($"UPDATE \"RoomTransitions\" SET \"CreatedUtc\" = {Date}, \"UpdatedUtc\" = {Date}, \"ArchivedUtc\" = CASE WHEN \"IsArchived\" THEN {Date} ELSE NULL END");
        await db.Database.ExecuteSqlAsync($"UPDATE \"SubroomConnections\" SET \"CreatedUtc\" = {Date}, \"UpdatedUtc\" = {Date}, \"ArchivedUtc\" = CASE WHEN \"IsArchived\" THEN {Date} ELSE NULL END");
        await db.Database.ExecuteSqlAsync($"UPDATE \"CheckLocations\" SET \"CreatedUtc\" = {Date}, \"UpdatedUtc\" = {Date}, \"ArchivedUtc\" = CASE WHEN \"IsArchived\" THEN {Date} ELSE NULL END");
        return (groupA.Id, groupB.Id, archived.Id, active.Id, empty.Id, mapPopulated.Id, mapNull.Id);
    }

    private static void AddChildren(LogicDbContext db, Guid roomId, bool? archived, DateTime date, int transitionId, int connectionId, int checkId)
    {
        var suffix = archived switch { true => "archived", false => "active", _ => "null" };
        var isArchived = archived == true;
        var populated = archived is not null;
        db.AddRange(
            new RoomTransition { Id = Id(transitionId), RoomId = roomId, Alias = $"transition-{suffix}", FriendlyName = "Transition", InGameId = populated ? "tid" : null, InGamePositionX = populated ? 1 : null, InGamePositionY = populated ? 2 : null, InGamePositionZ = populated ? 3 : null, LocalPositionX = populated ? 4 : null, LocalPositionY = populated ? 5 : null, LocalPositionZ = populated ? 6 : null, AnnotationSceneUnitX = populated ? 7 : null, AnnotationSceneUnitY = populated ? 8 : null, EnableAnnotation = populated, SourceSubroomReferenceText = populated ? "sub-active" : null, DestinationRoomReferenceText = populated ? "dest" : null, DestinationTransitionAliasText = populated ? "d" : null, Requirements = "req", Notes = "note", SortOrder = isArchived ? 3 : 0, IsTodo = populated, IsVerified = populated ? (isArchived ? false : true) : null, IsArchived = isArchived, ArchivedUtc = isArchived ? date : null, CreatedUtc = date, UpdatedUtc = date },
            new SubroomConnection { Id = Id(connectionId), RoomId = roomId, Alias = $"connection-{suffix}", FriendlyName = "Connection", SourceSubroomReferenceText = "sub-active", DestinationSubroomReferenceText = "sub-archived", Requirements = "req", Notes = "note", EnableAnnotation = populated, SceneUnitX = populated ? 1 : null, SceneUnitY = populated ? 2 : null, SortOrder = isArchived ? 3 : 0, IsTodo = populated, IsVerified = populated ? (isArchived ? false : true) : null, IsArchived = isArchived, ArchivedUtc = isArchived ? date : null, CreatedUtc = date, UpdatedUtc = date },
            new CheckLocation { Id = Id(checkId), RoomId = roomId, FriendlyName = $"Check {suffix}", InGameId = populated ? "cid" : null, InGamePositionX = populated ? 1 : null, InGamePositionY = populated ? 2 : null, InGamePositionZ = populated ? 3 : null, LocalPositionX = populated ? 4 : null, LocalPositionY = populated ? 5 : null, LocalPositionZ = populated ? 6 : null, AnnotationSceneUnitX = populated ? 7 : null, AnnotationSceneUnitY = populated ? 8 : null, SubroomReferenceText = populated ? "sub-active" : null, Requirements = "req", Notes = "note", IsIncludedInApworld = !populated, EnableAnnotation = populated, SortOrder = isArchived ? 3 : 0, IsTodo = populated, IsVerified = populated ? (isArchived ? false : true) : null, IsArchived = isArchived, ArchivedUtc = isArchived ? date : null, CreatedUtc = date, UpdatedUtc = date });
    }

    private sealed class NestedEntityFixture { public NestedWrapper Value { get; } = new(); }
    private sealed class NestedWrapper { public IReadOnlyList<Room> Rooms { get; } = []; }
    private sealed class GenericContextFixture { public Dictionary<string, IReadOnlyList<LogicDbContext>> Contexts { get; } = []; }
    private sealed class Factory(string path) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
}
