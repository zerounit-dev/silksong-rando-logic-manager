using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class DistributedImportPreparationTests
{
    private static readonly DateTime Utc = new(2026, 8, 17, 12, 34, 56, DateTimeKind.Utc);

    [Fact]
    public void Parser_AcceptsCompleteVersion2AndVersion1NullableActiveAndArchivedFixtures()
    {
        var v2 = Assert.IsType<DistributedImportPackageValidated>(new DistributedImportPackageParser().Parse(JsonSerializer.SerializeToUtf8Bytes(Version2(), new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Assert.Equal(2, v2.Package.ExportVersion); Assert.False(v2.Package.IsPartialRoomDump); Assert.Equal(1, v2.Summary.RoomCount);
        var room = Assert.Single(v2.Package.Rooms);
        Assert.Null(room.Contributors); Assert.True(room.IsArchived); Assert.Null(room.Transitions[0].IsVerified); Assert.False(room.Subrooms[0].EnableAnnotation);

        const string v1 = """{"exportVersion":1,"areaMap":{"maps":[{"id":"00000000-0000-0000-0000-000000000101","inGameId":"map","friendlyName":null,"sortOrder":0,"mapUnitMinX":null,"mapUnitMinY":null,"mapUnitMaxX":null,"mapUnitMaxY":null,"overlays":[],"zones":[{"id":"00000000-0000-0000-0000-000000000102","inGameId":"zone","friendlyName":null,"mapUnitMinX":null,"mapUnitMinY":null,"mapUnitMaxX":null,"mapUnitMaxY":null,"scenes":[{"id":"00000000-0000-0000-0000-000000000103","inGameId":"scene","friendlyName":null,"roomReferenceText":null,"chunks":[{"id":"00000000-0000-0000-0000-000000000104","cacheIndex":1,"initialState":null,"mapUnitMinX":null,"mapUnitMinY":null,"mapUnitMaxX":null,"mapUnitMaxY":null,"mapUnitZ":null}]}]}]}]},"roomGroupings":{"groups":[{"id":"00000000-0000-0000-0000-000000000105","friendlyName":"group","zoneReferenceText":null,"sortOrder":0,"createdUtc":"2026-08-17T12:34:56.0000000Z","updatedUtc":"2026-08-17T12:34:56.0000000Z"}]},"futureAddition":{"anything":true}}""";
        var parsedV1 = Assert.IsType<DistributedImportPackageValidated>(new DistributedImportPackageParser().Parse(Encoding.UTF8.GetBytes(v1)));
        Assert.Equal(1, parsedV1.Package.ExportVersion); Assert.Null(parsedV1.Package.IsPartialRoomDump); Assert.Single(parsedV1.Package.AreaMap!.Maps); Assert.Single(parsedV1.Package.RoomGroupings!.Groups);
    }

    [Theory]
    [InlineData("{\"exportVersion\":2,\"exportVersion\":2}", "duplicate property")]
    [InlineData("{\"exportVersion\":3}", "unsupported")]
    [InlineData("{\"exportVersion\":\"2\"}", "integer")]
    public void Parser_RejectsEnvelopeFailures(string json, string reason)
    {
        var rejected = Assert.IsType<DistributedImportPackageRejected>(new DistributedImportPackageParser().Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(reason, Assert.Single(rejected.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_RejectsMissingWrongTypedMalformedAndConflictingDocuments()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var valid = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(Version2(), options));
        foreach (var (json, expected) in new[]
        {
            (valid.Replace("\"friendlyName\":\"room\",", "", StringComparison.Ordinal), "required"),
            (valid.Replace("\"sortOrder\":2", "\"sortOrder\":\"2\"", StringComparison.Ordinal), "integer"),
            (valid.Replace("00000000-0000-0000-0000-000000000001", "not-a-guid", StringComparison.Ordinal), "GUID"),
            (valid.Replace("2026-08-17T12:34:56Z", "2026-08-17T12:34:56+01:00", StringComparison.Ordinal), "UTC timestamp"),
            (valid.Replace("00000000-0000-0000-0000-000000000011", "00000000-0000-0000-0000-000000000012", StringComparison.Ordinal), "reuses GUID")
        })
        {
            var rejected = Assert.IsType<DistributedImportPackageRejected>(new DistributedImportPackageParser().Parse(Encoding.UTF8.GetBytes(json)));
            Assert.Contains(expected, Assert.Single(rejected.Errors), StringComparison.OrdinalIgnoreCase);
        }

        var package = Version2();
        var second = package.Rooms[0] with { Id = Guid.Parse("00000000-0000-0000-0000-000000000098") };
        var ownerRejected = Assert.IsType<DistributedImportPackageRejected>(new DistributedImportPackageParser().Parse(JsonSerializer.SerializeToUtf8Bytes(package with { Rooms = [package.Rooms[0], second] }, options)));
        Assert.Contains("different room owner", Assert.Single(ownerRejected.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Parser_MigrationCurrentSqlite_RejectsDuplicateStructuralMapIdentitiesBeforeAnySelectedMapWrite()
    {
        var mapA = Guid.NewGuid(); var mapB = Guid.NewGuid(); var zoneA = Guid.NewGuid(); var zoneB = Guid.NewGuid(); var sceneA = Guid.NewGuid(); var sceneB = Guid.NewGuid(); var chunkA = Guid.NewGuid(); var chunkB = Guid.NewGuid();
        DistributedMap Map(Guid id, string mapId, IReadOnlyList<DistributedMapZone> zones) => new(id, mapId, null, 0, null, null, null, null, [], zones);
        DistributedMapZone Zone(Guid id, string zoneId, IReadOnlyList<DistributedMapScene> scenes) => new(id, zoneId, null, null, null, null, null, scenes);
        DistributedMapScene Scene(Guid id, string sceneId, IReadOnlyList<DistributedMapChunk> chunks) => new(id, sceneId, null, null, chunks);
        DistributedMapChunk Chunk(Guid id, int index) => new(id, index, null, null, null, null, null, null);
        var snapshots = new DistributedAreaMapSnapshot[]
        {
            new([Map(mapA, "map", []), Map(mapB, "MAP", [])]),
            new([Map(mapA, "map", [Zone(zoneA, "zone", []), Zone(zoneB, "ZONE", [])])]),
            new([Map(mapA, "map", [Zone(zoneA, "zone", [Scene(sceneA, "scene", []), Scene(sceneB, "SCENE", [])])])]),
            new([Map(mapA, "map", [Zone(zoneA, "zone", [Scene(sceneA, "scene", [Chunk(chunkA, 1), Chunk(chunkB, 1)])])])])
        };
        var path = Path.Combine(Path.GetTempPath(), $"silksong-duplicate-map-identity-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var db = new LogicDbContext(options)) await db.Database.MigrateAsync();
            foreach (var snapshot in snapshots)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(new { exportVersion = 1, areaMap = snapshot }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.IsType<DistributedImportPackageRejected>(new DistributedImportPackageParser().Parse(json));
                await using var verify = new LogicDbContext(options); Assert.Equal(0, await verify.Maps.CountAsync()); Assert.Equal(0, await verify.MapZones.CountAsync()); Assert.Equal(0, await verify.MapScenes.CountAsync()); Assert.Equal(0, await verify.MapChunks.CountAsync());
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Comparison_UsesExactStructureAndProvidesNeutralCurrentIncomingAndSameGameWarning()
    {
        var incoming = Version2().Rooms.Single();
        var current = incoming with { UpdatedUtc = incoming.UpdatedUtc, FriendlyName = "master", Subrooms = [incoming.Subrooms[0] with { Notes = "master note" }], Transitions = [], Connections = [], Checks = [] };
        var comparison = DistributedRoomComparisonService.Compare(current, incoming, [current, incoming with { Id = Guid.Parse("00000000-0000-0000-0000-000000000099"), FriendlyName = "other" }]);
        Assert.Equal(DistributedRoomComparisonKind.Changed, comparison.Kind);
        Assert.Contains(comparison.TopLevelFields, x => x.FieldName == "friendlyName" && x.Evidence == DistributedComparisonEvidence.Changed);
        Assert.Contains(comparison.TopLevelFields, x => x.FieldName == "updatedUtc" && x.Evidence == DistributedComparisonEvidence.Neutral);
        Assert.Contains(comparison.Subrooms, x => x.Evidence == DistributedComparisonEvidence.Changed);
        Assert.All(comparison.Transitions, x => Assert.Equal(DistributedComparisonEvidence.Incoming, x.Evidence));
        Assert.Single(comparison.Warnings);
        Assert.Equal(DistributedRoomComparisonKind.New, DistributedRoomComparisonService.Compare(null, incoming, []).Kind);
        Assert.Equal(DistributedRoomComparisonKind.Identical, DistributedRoomComparisonService.Compare(incoming, incoming, []).Kind);
    }

    [Fact]
    public void ProjectionMapper_MapsEveryPersistedExchangeField_AndImportContractsContainNoEfTypes()
    {
        var archivedUtc = Utc.AddDays(-3);
        var createdUtc = Utc.AddDays(-2);
        var updatedUtc = Utc.AddDays(-1);
        var projection = new DistributedImportRoomProjection(Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), "room-ref", "room", "room-game", "contributors", "comments", 1.1, 2.2, 3.3, 4.4, 5.5, 6.6, true, 7, true, archivedUtc, createdUtc, updatedUtc,
            [new(Guid.Parse("00000000-0000-0000-0000-000000000011"), "sub-ref", "sub", "sub notes", 1.1, null, 3.3, 4.4, false, 1, true, archivedUtc, createdUtc, updatedUtc)],
            [new(Guid.Parse("00000000-0000-0000-0000-000000000012"), "T", "transition", "transition-game", 1.1, 2.2, 3.3, 4.4, 5.5, 6.6, 7.7, null, true, "source", "destination-room", "D", "transition requirements", "transition notes", 2, true, false, true, archivedUtc, createdUtc, updatedUtc)],
            [new(Guid.Parse("00000000-0000-0000-0000-000000000013"), "C", "connection", "from", "to", "connection requirements", "connection notes", true, null, 2.2, 3, true, null, true, archivedUtc, createdUtc, updatedUtc)],
            [new(Guid.Parse("00000000-0000-0000-0000-000000000014"), "check", "check-game", 1.1, 2.2, 3.3, 4.4, 5.5, 6.6, null, 8.8, "subroom", "check requirements", "check notes", false, true, 4, true, null, true, archivedUtc, createdUtc, updatedUtc)]);
        var expected = new DistributedRoomDocument(projection.Id, projection.RoomGroupId, projection.ReferenceId, projection.FriendlyName, projection.InGameId, projection.Contributors, projection.Comments, projection.SceneUnitWidth, projection.SceneUnitHeight, projection.SceneImageScaleXPercent, projection.SceneImageScaleYPercent, projection.SceneImagePanXPercent, projection.SceneImagePanYPercent, projection.IsSceneImageStale, projection.SortOrder, projection.IsArchived, projection.ArchivedUtc, projection.CreatedUtc, projection.UpdatedUtc,
            [new(projection.Subrooms[0].Id, projection.Subrooms[0].ReferenceId, projection.Subrooms[0].FriendlyName, projection.Subrooms[0].Notes, projection.Subrooms[0].SceneUnitX, projection.Subrooms[0].SceneUnitY, projection.Subrooms[0].SceneUnitWidth, projection.Subrooms[0].SceneUnitHeight, projection.Subrooms[0].EnableAnnotation, projection.Subrooms[0].SortOrder, projection.Subrooms[0].IsArchived, projection.Subrooms[0].ArchivedUtc, projection.Subrooms[0].CreatedUtc, projection.Subrooms[0].UpdatedUtc)],
            [new(projection.Transitions[0].Id, projection.Transitions[0].Alias, projection.Transitions[0].FriendlyName, projection.Transitions[0].InGameId, projection.Transitions[0].InGamePositionX, projection.Transitions[0].InGamePositionY, projection.Transitions[0].InGamePositionZ, projection.Transitions[0].LocalPositionX, projection.Transitions[0].LocalPositionY, projection.Transitions[0].LocalPositionZ, projection.Transitions[0].AnnotationSceneUnitX, projection.Transitions[0].AnnotationSceneUnitY, projection.Transitions[0].EnableAnnotation, projection.Transitions[0].SourceSubroomReferenceText, projection.Transitions[0].DestinationRoomReferenceText, projection.Transitions[0].DestinationTransitionAliasText, projection.Transitions[0].Requirements, projection.Transitions[0].Notes, projection.Transitions[0].SortOrder, projection.Transitions[0].IsTodo, projection.Transitions[0].IsVerified, projection.Transitions[0].IsArchived, projection.Transitions[0].ArchivedUtc, projection.Transitions[0].CreatedUtc, projection.Transitions[0].UpdatedUtc)],
            [new(projection.Connections[0].Id, projection.Connections[0].Alias, projection.Connections[0].FriendlyName, projection.Connections[0].SourceSubroomReferenceText, projection.Connections[0].DestinationSubroomReferenceText, projection.Connections[0].Requirements, projection.Connections[0].Notes, projection.Connections[0].EnableAnnotation, projection.Connections[0].SceneUnitX, projection.Connections[0].SceneUnitY, projection.Connections[0].SortOrder, projection.Connections[0].IsTodo, projection.Connections[0].IsVerified, projection.Connections[0].IsArchived, projection.Connections[0].ArchivedUtc, projection.Connections[0].CreatedUtc, projection.Connections[0].UpdatedUtc)],
            [new(projection.Checks[0].Id, projection.Checks[0].FriendlyName, projection.Checks[0].InGameId, projection.Checks[0].InGamePositionX, projection.Checks[0].InGamePositionY, projection.Checks[0].InGamePositionZ, projection.Checks[0].LocalPositionX, projection.Checks[0].LocalPositionY, projection.Checks[0].LocalPositionZ, projection.Checks[0].AnnotationSceneUnitX, projection.Checks[0].AnnotationSceneUnitY, projection.Checks[0].SubroomReferenceText, projection.Checks[0].Requirements, projection.Checks[0].Notes, projection.Checks[0].IsIncludedInApworld, projection.Checks[0].EnableAnnotation, projection.Checks[0].SortOrder, projection.Checks[0].IsTodo, projection.Checks[0].IsVerified, projection.Checks[0].IsArchived, projection.Checks[0].ArchivedUtc, projection.Checks[0].CreatedUtc, projection.Checks[0].UpdatedUtc)]);
        var mapped = DistributedImportProjectionMapper.MapRoom(projection);
        Assert.Equivalent(expected, mapped, strict: true);

        AssertNoBoundaryViolations(ImportPresentationContracts());
    }

    [Fact]
    public void DistributedImportPresentationAndWizardContracts_RecursivelyExcludeEfEntitiesAndDbContexts()
    {
        var contracts = ImportPresentationContracts()
            .Append(typeof(Silksong_Rando_Logic_Manager.Components.DistributedImportWizard))
            .Append(typeof(SceneImageRebuildRequest))
            .Append(typeof(SceneImageRebuildResult))
            .Append(typeof(Silksong_Rando_Logic_Manager.Components.ChildEvidenceTable<>));

        AssertNoBoundaryViolations(contracts);
    }

    [Fact]
    public void Wizard_SceneImageAction_UsesTypedServiceAndDoesNotMutateReviewState()
    {
        var source = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "DistributedImportWizard.razor"));
        Assert.Contains("@inject SceneImageCaptureService SceneImages", source, StringComparison.Ordinal);
        Assert.Contains("RebuildPackageRoomsAsync", source, StringComparison.Ordinal);
        var action = source[source.LastIndexOf("GenerateSceneImagesAsync", StringComparison.Ordinal)..source.IndexOf("private void Previous", StringComparison.Ordinal)];
        Assert.DoesNotContain("view.RoomIndex", action, StringComparison.Ordinal);
        Assert.DoesNotContain("view.CurrentComparison", action, StringComparison.Ordinal);
        Assert.DoesNotContain("view.State =", action, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePackageReadPath_SuppliesTheExact128MiBStreamLimit()
    {
        var source = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "DistributedImportWizard.razor"));

        Assert.Contains("args.File.OpenReadStream(128 * 1024 * 1024)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("args.File.OpenReadStream()", source, StringComparison.Ordinal);
    }

    private static void AssertNoBoundaryViolations(IEnumerable<Type> contracts)
    {
        var violations = DistributedImportContractBoundary.FindViolations(contracts);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<Type> ImportPresentationContracts() =>
        typeof(DistributedImportPackage).Assembly.GetTypes()
            .Where(type => type.Namespace == "Silksong_Rando_Logic_Manager.Services"
                && type.Name.StartsWith("Distributed", StringComparison.Ordinal)
                && !type.Name.EndsWith("Service", StringComparison.Ordinal)
                && type != typeof(DistributedImportPackageParser)
                && type != typeof(DistributedImportProjectionLoader));

    private static string FindSolutionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Silksong Rando Logic Manager.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Solution root was not found.");
    }

    [Fact]
    public async Task ProjectionLoader_MigrationCurrentSqlite_ProjectsActiveArchivedAndNullableRowsWithoutEntities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-import-projection-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var db = new LogicDbContext(options))
            {
                await db.Database.MigrateAsync();
                var room = new Room { Id = Guid.Parse("00000000-0000-0000-0000-000000000201"), ReferenceId = "ref", FriendlyName = "room", Contributors = null, Comments = null, IsArchived = true, CreatedUtc = Utc, UpdatedUtc = Utc, ArchivedUtc = Utc };
                db.Add(room);
                db.Add(new Subroom { Id = Guid.Parse("00000000-0000-0000-0000-000000000202"), RoomId = room.Id, ReferenceId = "sub", FriendlyName = "sub", Notes = null, EnableAnnotation = false, IsArchived = true, ArchivedUtc = Utc, CreatedUtc = Utc, UpdatedUtc = Utc });
                db.Add(new RoomTransition { Id = Guid.Parse("00000000-0000-0000-0000-000000000203"), RoomId = room.Id, Alias = "T", FriendlyName = "transition", Requirements = "", Notes = "", IsVerified = null, CreatedUtc = Utc, UpdatedUtc = Utc });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlAsync($"UPDATE \"Rooms\" SET \"CreatedUtc\" = {Utc}, \"UpdatedUtc\" = {Utc}, \"ArchivedUtc\" = {Utc} WHERE \"Id\" = {room.Id}");
            }
            var loader = new DistributedImportProjectionLoader(new TestFactory(path));
            var projection = await loader.LoadRoomAsync(Guid.Parse("00000000-0000-0000-0000-000000000201"));
            var mapped = DistributedImportProjectionMapper.MapRoom(Assert.IsType<DistributedImportRoomProjection>(projection));
            Assert.True(mapped.IsArchived); Assert.Null(mapped.Contributors); Assert.True(Assert.Single(mapped.Subrooms).IsArchived); Assert.Null(Assert.Single(mapped.Transitions).IsVerified);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    private static Version2RoomExportEnvelope Version2()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var sub = new DistributedSubroom(Guid.Parse("00000000-0000-0000-0000-000000000011"), "sub", "sub", null, null, null, null, null, false, 0, true, Utc, Utc, Utc);
        var transition = new DistributedTransition(Guid.Parse("00000000-0000-0000-0000-000000000012"), "t", "transition", null, null,null,null,null,null,null,null,null,false,null,null,null,"req","notes",0,false,null,false,null,Utc,Utc);
        var connection = new DistributedConnection(Guid.Parse("00000000-0000-0000-0000-000000000013"), "c", "connection", "from", "to", "req", "notes", false, null, null, 0, false, null, false, null, Utc, Utc);
        var check = new DistributedCheck(Guid.Parse("00000000-0000-0000-0000-000000000014"), "check", null, null,null,null,null,null,null,null,null,null,"req","notes",true,false,0,false,null,false,null,Utc,Utc);
        return new(2, false, [new DistributedRoomDocument(id, null, "ref", "room", "game", null, null, null,null,null,null,null,null,false,2,true,Utc,Utc,Utc,[sub],[transition],[connection],[check])], new DistributedRoomGroupingSnapshot([]), null);
    }
    private sealed class TestFactory(string path) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}

internal static class DistributedImportContractBoundary
{
    private static readonly Type[] prohibited =
    [
        typeof(DbContext), typeof(Room), typeof(RoomGroup), typeof(Subroom), typeof(RoomTransition),
        typeof(SubroomConnection), typeof(CheckLocation), typeof(Map), typeof(MapOverlay), typeof(MapZone),
        typeof(MapScene), typeof(MapChunk)
    ];

    public static IReadOnlyList<string> FindViolations(IEnumerable<Type> roots)
    {
        var violations = new List<string>();
        foreach (var root in roots)
            Inspect(root, root.FullName ?? root.Name, [], violations);
        return violations;
    }

    private static void Inspect(Type type, string path, HashSet<Type> seen, List<string> violations)
    {
        while (type.HasElementType) type = type.GetElementType()!;
        if (prohibited.Any(candidate => candidate.IsAssignableFrom(type)))
        {
            violations.Add($"{path} exposes prohibited {type.FullName}.");
            return;
        }

        foreach (var argument in type.GetGenericArguments())
            Inspect(argument, $"{path}<{argument.Name}>", new(seen), violations);

        if (!ShouldInspect(type) || !seen.Add(type)) return;
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(members))
        {
            var memberPath = $"{path}.{field.Name}";
            if (IsInjectedField(type, field)) InspectSignature(field.FieldType, memberPath, violations);
            else Inspect(field.FieldType, memberPath, new(seen), violations);
        }
        foreach (var property in type.GetProperties(members).Where(property => property.GetIndexParameters().Length == 0))
        {
            var memberPath = $"{path}.{property.Name}";
            if (property.IsDefined(typeof(InjectAttribute), inherit: true)) InspectSignature(property.PropertyType, memberPath, violations);
            else Inspect(property.PropertyType, memberPath, new(seen), violations);
        }
    }

    private static void InspectSignature(Type type, string path, List<string> violations)
    {
        while (type.HasElementType) type = type.GetElementType()!;
        if (prohibited.Any(candidate => candidate.IsAssignableFrom(type)))
        {
            violations.Add($"{path} exposes prohibited {type.FullName}.");
            return;
        }

        foreach (var argument in type.GetGenericArguments())
            InspectSignature(argument, $"{path}<{argument.Name}>", violations);
    }

    private static bool IsInjectedField(Type owner, FieldInfo field)
    {
        if (field.IsDefined(typeof(InjectAttribute), inherit: true)) return true;
        const string suffix = "k__BackingField";
        if (!field.Name.StartsWith('<') || !field.Name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var propertyName = field.Name[1..field.Name.IndexOf('>')];
        return owner.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.IsDefined(typeof(InjectAttribute), inherit: true) == true;
    }

    private static bool ShouldInspect(Type type) => type.Assembly == typeof(DistributedImportPackage).Assembly;
}
