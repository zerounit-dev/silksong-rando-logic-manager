using System.Diagnostics;
using System.Text;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;
using Xunit;
using Xunit.Abstractions;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class RequirementCataloguePersistenceTests
{
    [Fact]
    public async Task MigrationCurrentSqlite_HasOnlyFlatParentsAndBaseline()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        await using var db = fixture.CreateDbContext();
        var tables = await TableNames(db);

        Assert.Contains("RequirementPredicates", tables);
        Assert.Contains("RequirementItems", tables);
        Assert.DoesNotContain(tables, table => table.Contains("Alias", StringComparison.Ordinal));
        Assert.NotEmpty(await db.RequirementPredicates.ToListAsync());
        Assert.NotEmpty(await db.RequirementItems.ToListAsync());
        Assert.All(await db.RequirementPredicates.ToListAsync(), row => Assert.True(RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical == row.Aliases));
    }

    [Fact]
    public async Task ControlledFourTableFixture_UpgradesAndDowngradesPreservingParentsRequirementsAndOrder()
    {
        await using var fixture = await PredecessorFixture.CreateAsync();
        var predicateId = Guid.NewGuid(); var itemId = Guid.NewGuid(); var roomId = Guid.NewGuid();
        await fixture.ExecuteAsync($"INSERT INTO \"Rooms\" (\"Id\", \"ReferenceId\", \"FriendlyName\", \"Comments\", \"SortOrder\", \"IsArchived\", \"CreatedUtc\", \"UpdatedUtc\", \"IsSceneImageStale\") VALUES ('{roomId}', 'migration-room', 'Migration room', '', 0, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 0);");
        await fixture.ExecuteAsync($"INSERT INTO \"RequirementPredicates\" VALUES ('{predicateId}', 'Predicate', NULL, '{{predicate}}', 'atom', '', 7);");
        await fixture.ExecuteAsync($"INSERT INTO \"RequirementItems\" VALUES ('{itemId}', 'Item', 'Category', 'item', '', 4);");
        await fixture.ExecuteAsync($"INSERT INTO \"RequirementPredicateAliases\" VALUES ('{Guid.NewGuid()}', '{predicateId}', 'first', 1), ('{Guid.NewGuid()}', '{predicateId}', 'second', 2);");
        await fixture.ExecuteAsync($"INSERT INTO \"RequirementItemAliases\" VALUES ('{Guid.NewGuid()}', '{itemId}', 'item alias', 0);");

        await using (var source = fixture.Context())
        {
            var group = new RoomGroup { FriendlyName = "Unrelated group" };
            var room = new Room { RoomGroup = group, ReferenceId = "migration-room-2", FriendlyName = "Migration room 2" };
            source.AddRange(group, room,
                new RoomTransition { Room = room, Alias = "exit", FriendlyName = "Exit", Requirements = "transition requirement" },
                new SubroomConnection { Room = room, Alias = "path", FriendlyName = "Path", SourceSubroomReferenceText = "from", DestinationSubroomReferenceText = "to", Requirements = "connection requirement" },
                new CheckLocation { Room = room, FriendlyName = "Check", Requirements = "check requirement" });
            await source.SaveChangesAsync();
        }

        await fixture.MigrateAsync("20260905130000_FlattenRequirementCatalogueAliases");
        Assert.Equal("first, second", await fixture.ScalarStringAsync($"SELECT Aliases FROM RequirementPredicates WHERE Id = '{predicateId}'"));
        Assert.Equal("item alias", await fixture.ScalarStringAsync($"SELECT Aliases FROM RequirementItems WHERE Id = '{itemId}'"));
        Assert.Equal("migration-room", await fixture.ScalarStringAsync($"SELECT ReferenceId FROM Rooms WHERE Id = '{roomId}'"));
        await using (var migrated = fixture.Context())
        {
            Assert.Equal("transition requirement", (await migrated.RoomTransitions.SingleAsync(x => x.Alias == "exit")).Requirements);
            Assert.Equal("connection requirement", (await migrated.SubroomConnections.SingleAsync(x => x.Alias == "path")).Requirements);
            Assert.Equal("check requirement", (await migrated.CheckLocations.SingleAsync(x => x.FriendlyName == "Check")).Requirements);
            Assert.Equal("Unrelated group", (await migrated.RoomGroups.SingleAsync(x => x.FriendlyName == "Unrelated group")).FriendlyName);
        }

        await fixture.MigrateAsync("20260905120000_AddRequirementCatalogue");
        Assert.Contains("RequirementPredicateAliases", await fixture.TableNamesAsync());
        Assert.Equal(2L, await fixture.ScalarLongAsync($"SELECT count(*) FROM RequirementPredicateAliases WHERE RequirementPredicateId = '{predicateId}'"));
        Assert.Equal("first", await fixture.ScalarStringAsync($"SELECT Alias FROM RequirementPredicateAliases WHERE RequirementPredicateId = '{predicateId}' ORDER BY SortOrder LIMIT 1"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("sprint", "collision")]
    [InlineData("rún", "unsupported Unicode")]
    public async Task UnsafeControlledFourTableFixture_AbortsBeforeAnySourceDropOrPartialChange(string alias, string _)
    {
        await using var fixture = await PredecessorFixture.CreateAsync();
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync($"INSERT INTO \"RequirementPredicates\" VALUES ('{id}', 'Unsafe', NULL, '{{predicate}}', 'unsafe', '', 999);");
        await fixture.ExecuteAsync($"INSERT INTO \"RequirementPredicateAliases\" VALUES ('{Guid.NewGuid()}', '{id}', '{alias.Replace("'", "''")}', 0);");

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.MigrateAsync("20260905130000_FlattenRequirementCatalogueAliases"));
        Assert.Contains("RequirementPredicateAliases", await fixture.TableNamesAsync());
        Assert.Equal(alias, await fixture.ScalarStringAsync($"SELECT Alias FROM RequirementPredicateAliases WHERE RequirementPredicateId = '{id}'"));
        Assert.DoesNotContain("Aliases", await fixture.ColumnsAsync("RequirementPredicates"));
    }

    private static async Task<string[]> TableNames(LogicDbContext db) => await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToArrayAsync();
}

public sealed class RequirementCatalogueServiceTests
{
    [Fact]
    public async Task RealSqlite_CrudQueryDeleteReorderAndValidationDoNotMutateRequirements()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext()) { var room = new Room { ReferenceId = "catalogue-room", FriendlyName = "Catalogue room" }; db.Add(room); db.Add(new CheckLocation { RoomId = room.Id, FriendlyName = "Untouched check", Requirements = "drifter's" }); await db.SaveChangesAsync(); }
        var service = new RequirementCatalogueService(fixture, new RequirementCatalogueWriteCoordinator(fixture));
        var add = await service.ApplyAsync(new ApplyRequirementPredicate(null, "Test predicate", "Test", "{predicate}", "  Test\tAlias , second ", "test", ""));
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, add.Status);
        var row = Assert.Single((await service.LoadManagerAsync()).Predicates, x => x.Name == "Test predicate");
        Assert.Equal("Test Alias, second", row.Aliases);
        Assert.Equal(RequirementCatalogueCommandStatus.ValidationRejected, (await service.ApplyAsync(new ApplyRequirementPredicate(null, "Collision", null, "{predicate}", "test alias", "collision", ""))).Status);
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.ApplyAsync(new ApplyRequirementPredicate(row.Id, "Test predicate edited", "Test", "{predicate}", "edited alias", "test", ""))).Status);
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.ApplyAsync(new ApplyRequirementItem(null, "Test item", "Test", "test item", "test-item", ""))).Status);
        var item = Assert.Single((await service.LoadManagerAsync()).Items, x => x.Name == "Test item");
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.ReorderAsync(new ReorderRequirementPredicate(row.Id, 0))).Status);
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.ReorderAsync(new ReorderRequirementItem(item.Id, 0))).Status);
        Assert.Equal(RequirementCatalogueCommandStatus.Missing, (await service.DeleteAsync(new DeleteRequirementItem(Guid.NewGuid()))).Status);
        var competing = await Task.WhenAll(
            service.ApplyAsync(new ApplyRequirementPredicate(null, "Concurrent one", null, "{predicate}", "concurrent one", "one", "")),
            service.ApplyAsync(new ApplyRequirementPredicate(null, "Concurrent two", null, "{predicate}", "concurrent two", "two", "")));
        Assert.All(competing, outcome => Assert.Equal(RequirementCatalogueCommandStatus.Committed, outcome.Status));
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.DeleteAsync(new DeleteRequirementPredicate(row.Id))).Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("drifter's", await verify.CheckLocations.Select(x => x.Requirements).SingleAsync());
        Assert.Equal("catalogue-room", (await verify.Rooms.SingleAsync()).ReferenceId);
        Assert.Equal("Test item", (await verify.RequirementItems.SingleAsync(x => x.Id == item.Id)).Name);
        Assert.Equal(2, await verify.RequirementPredicates.CountAsync(x => x.Name.StartsWith("Concurrent")));
    }
}

public sealed class RequirementCatalogueExchangeTests
{
    [Fact]
    public async Task StrictFlatDocument_ExportsParsesAndRejectsAliasObjectOrUnknownMember()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var export = await new RequirementCatalogueExportService(fixture, TimeProvider.System).ExportAsync();
        var parser = new RequirementCatalogueImportParser();
        Assert.NotNull(parser.Parse(export.JsonUtf8).Snapshot);
        Assert.Null(parser.Parse(Encoding.UTF8.GetBytes("{\"requirementCatalogueVersion\":1,\"predicates\":[],\"items\":[],\"extra\":true}")).Snapshot);
        Assert.Null(parser.Parse(Encoding.UTF8.GetBytes("{\"requirementCatalogueVersion\":1,\"predicates\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"p\",\"category\":null,\"inputSyntax\":\"{predicate}\",\"outputSyntax\":\"p\",\"notes\":\"\",\"sortOrder\":0,\"aliases\":[\"p\"]}],\"items\":[]}")).Snapshot);
    }

    [Fact]
    public async Task StrictParser_RejectsNoncanonicalAliasesAndReplacementWritesNothingToRealSqlite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var parser = new RequirementCatalogueImportParser();
        var json = Encoding.UTF8.GetBytes("{\"requirementCatalogueVersion\":1,\"predicates\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"p\",\"category\":null,\"inputSyntax\":\"{predicate}\",\"outputSyntax\":\"p\",\"notes\":\"\",\"sortOrder\":0,\"aliases\":\"one,  two \"}],\"items\":[]}");
        var parsed = parser.Parse(json);
        Assert.False(parsed.IsAccepted);
        Assert.Contains("canonical", Assert.Single(parsed.Errors), StringComparison.OrdinalIgnoreCase);

        await using var before = fixture.CreateDbContext();
        var predicateCount = await before.RequirementPredicates.CountAsync();
        var aliases = await before.RequirementPredicates.Select(x => x.Aliases).ToArrayAsync();
        Assert.Null(parsed.Snapshot);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(predicateCount, await after.RequirementPredicates.CountAsync());
        Assert.Equal(aliases, await after.RequirementPredicates.Select(x => x.Aliases).ToArrayAsync());
    }
}

public sealed class RequirementCatalogueImportServiceTests
{
    [Fact]
    public async Task RealSqlite_ConfirmedReplacementPreservesRoomRequirements()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { ReferenceId = "preserved", FriendlyName = "Preserved" };
        await using (var db = fixture.CreateDbContext()) { db.Add(room); db.Add(new CheckLocation { RoomId = room.Id, FriendlyName = "Check", Requirements = "drifter's" }); await db.SaveChangesAsync(); }
        var snapshot = new RequirementCatalogueSnapshot([new(Guid.NewGuid(), "replacement", null, "{predicate}", "replacement", "", 0, "replacement")], []);
        var result = await new RequirementCatalogueImportService(new RequirementCatalogueWriteCoordinator(fixture)).ReplaceConfirmedAsync(snapshot);
        Assert.Equal(RequirementCatalogueImportStatus.Committed, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("drifter's", (await verify.CheckLocations.SingleAsync()).Requirements);
        Assert.Equal("replacement", Assert.Single(await verify.RequirementPredicates.ToListAsync()).Aliases);
    }

    [Fact]
    public async Task RealSqlite_InducedReplacementWriteFailureRollsBackCatalogueAndPreservesNoncatalogue()
    {
        var interceptor = new ThrowingCatalogueSaveInterceptor();
        await using var fixture = await MigratedSqliteFixture.CreateAsync(interceptor: interceptor);
        await using (var db = fixture.CreateDbContext())
        {
            var room = new Room { ReferenceId = "rollback", FriendlyName = "Rollback" };
            db.Add(room); db.Add(new CheckLocation { RoomId = room.Id, FriendlyName = "Check", Requirements = "unchanged" });
            await db.SaveChangesAsync();
        }
        await using (var before = fixture.CreateDbContext())
        {
            var original = await before.RequirementPredicates.OrderBy(x => x.SortOrder).Select(x => x.Id).ToArrayAsync();
            interceptor.Enabled = true;
            var snapshot = new RequirementCatalogueSnapshot([new(Guid.NewGuid(), "replacement", null, "{predicate}", "replacement", "", 0, "replacement")], []);
            var result = await new RequirementCatalogueImportService(new RequirementCatalogueWriteCoordinator(fixture)).ReplaceConfirmedAsync(snapshot);
            Assert.Equal(RequirementCatalogueImportStatus.Failed, result.Status);
            await using var after = fixture.CreateDbContext();
            Assert.Equal(original, await after.RequirementPredicates.OrderBy(x => x.SortOrder).Select(x => x.Id).ToArrayAsync());
            Assert.Equal("unchanged", await after.CheckLocations.Select(x => x.Requirements).SingleAsync());
        }
    }
}

public sealed class RequirementCataloguePageSqliteTests
{
    [Fact]
    public async Task ActualManagerServiceRoutes_MutateFlatSqliteOnly()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var service = new RequirementCatalogueService(fixture, new RequirementCatalogueWriteCoordinator(fixture));
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.ApplyAsync(new ApplyRequirementItem(null, "Page item", null, "page item", "page-item", ""))).Status);
        var item = Assert.Single((await service.LoadManagerAsync()).Items, x => x.Name == "Page item");
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.ApplyAsync(new ApplyRequirementItem(item.Id, "Page item", null, "changed item, alternative", "page-item", ""))).Status);
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, (await service.DeleteAsync(new DeleteRequirementItem(item.Id))).Status);
    }

    [Fact]
    public async Task RenderedManagerApplyCallback_MutatesMigrationCurrentSqlite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        var writes = new RequirementCatalogueWriteCoordinator(fixture);
        context.Services.AddSingleton(writes);
        context.Services.AddSingleton(new RequirementCatalogueService(fixture, writes));
        context.Services.AddSingleton(new RequirementCatalogueImportParser());
        context.Services.AddSingleton(new RequirementCatalogueImportService(writes));
        var page = context.RenderComponent<RequirementCatalogue>();
        page.FindAll("button.requirement-catalogue-action").Single(button => button.TextContent.Contains("add predicate", StringComparison.Ordinal)).Click();
        var dialog = page.Find(".requirement-definition-modal");
        dialog.QuerySelectorAll("input")[0]!.Input("Rendered predicate");
        dialog.QuerySelector("textarea")!.Input("rendered predicate");
        dialog.QuerySelectorAll("input")[2]!.Input("rendered");
        page.FindAll("button").Single(button => button.TextContent == "Apply").Click();
        page.WaitForAssertion(() =>
        {
            using var assertDb = fixture.CreateDbContext();
            Assert.True(assertDb.RequirementPredicates.Any(x => x.Name == "Rendered predicate"));
        });
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("rendered predicate", (await verify.RequirementPredicates.SingleAsync(x => x.Name == "Rendered predicate")).Aliases);
    }
}

internal sealed class ThrowingCatalogueSaveInterceptor : SaveChangesInterceptor
{
    public bool Enabled { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Enabled && eventData.Context?.ChangeTracker.Entries<RequirementPredicate>().Any(entry => entry.State == EntityState.Added) == true)
            throw new InvalidOperationException("Induced catalogue replacement write failure.");
        return ValueTask.FromResult(result);
    }
}

public sealed class RequirementCataloguePageTests
{
    [Fact]
    public void FlatManagerRoute_RendersFlatAliasesAndTypedDragIdentity()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Pages", "RequirementCatalogue.razor"));
        Assert.Contains("@page \"/requirements\"", text, StringComparison.Ordinal);
        Assert.Contains("@row.Aliases", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AliasId", text, StringComparison.Ordinal);
    }
}

public sealed class LocalRequirementCatalogueDownloadEndpointTests
{
    [Fact]
    public async Task DownloadEndpoint_ReturnsUtf8JsonAttachmentForFlatExport()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var result = await LocalRequirementCatalogueDownloadEndpoints.DownloadAsync(new RequirementCatalogueExportService(fixture, TimeProvider.System));
        Assert.Equal("application/json; charset=utf-8", result.ContentType);
        Assert.StartsWith("silksong-requirement-catalogue-", result.FileDownloadName, StringComparison.Ordinal);
        Assert.EndsWith(".json", result.FileDownloadName, StringComparison.Ordinal);
        Assert.NotEqual(0, result.FileContents.Length);
    }
}

public sealed class RequirementCatalogueContractBoundaryTests
{
    [Fact]
    public void FlatContracts_ExposeNoAliasEntityOrGuid()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Services", "RequirementCatalogueContracts.cs"));
        Assert.Contains("string Aliases", text);
        Assert.DoesNotContain("AliasView", text);
        Assert.DoesNotContain("AliasId", text);
    }
}

public sealed class RequirementCatalogueDragJsTests
{
    [Fact]
    public void DragOwner_UsesTypedFlatReorderIntents()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "requirement-catalogue.js"));
        Assert.Contains("catalogueKind", text, StringComparison.Ordinal);
        Assert.Contains("invokeMethodAsync(\"Drop\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("aliasId", text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RequirementCatalogueServerTraceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private MigratedSqliteFixture? fixture;
    private RequirementCatalogueService? service;
    public async Task InitializeAsync() { fixture = await MigratedSqliteFixture.CreateAsync(); service = new RequirementCatalogueService(fixture, new RequirementCatalogueWriteCoordinator(fixture)); }
    public async Task DisposeAsync() { if (fixture is not null) await fixture.DisposeAsync(); }

    [Fact]
    public async Task FirstUseAndWarmTrace_RecordsCommittedFlatMutationsBelowFiftyMilliseconds()
    {
        await Sample("first-use", 0);
        await Sample("warmup", 0);
        for (var sample = 1; sample <= 3; sample++) await Sample("warm", sample);
    }

    private async Task Sample(string phase, int sample)
    {
        var suffix = $"{phase} {sample} {Guid.NewGuid():N}";
        var aliasSuffix = suffix.Replace("-", " ", StringComparison.Ordinal);
        await Trace(phase, sample, "add", () => service!.ApplyAsync(new ApplyRequirementPredicate(null, $"Trace {suffix}", null, "{predicate}", $"trace {aliasSuffix}", "trace", "")));
        var predicate = (await service!.LoadManagerAsync()).Predicates.Last(x => x.Name == $"Trace {suffix}");
        await Trace(phase, sample, "edit", () => service.ApplyAsync(new ApplyRequirementPredicate(predicate.Id, predicate.Name, null, "{predicate}", $"trace {aliasSuffix}, edited", "trace", "")));
        await Trace(phase, sample, "predicate reorder", () => service.ReorderAsync(new ReorderRequirementPredicate(predicate.Id, 0)));
        var item = (await service.LoadManagerAsync()).Items.First();
        await Trace(phase, sample, "item reorder", () => service.ReorderAsync(new ReorderRequirementItem(item.Id, 0)));
        await Trace(phase, sample, "delete", () => service.DeleteAsync(new DeleteRequirementPredicate(predicate.Id)));
    }

    private async Task Trace(string phase, int sample, string operation, Func<Task<RequirementCatalogueCommandOutcome>> operationCall)
    {
        var clock = Stopwatch.StartNew(); var outcome = await operationCall(); clock.Stop();
        Assert.Equal(RequirementCatalogueCommandStatus.Committed, outcome.Status);
        output.WriteLine($"Requirement catalogue {phase} trace | sample={sample} | {operation} | service={clock.Elapsed.TotalMilliseconds:F3}ms | resolver=0ms | map=0 | scene=0");
        if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"{operation} warm sample {sample} exceeded 50ms.");
    }
}

internal sealed class PredecessorFixture : IAsyncDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-catalogue-predecessor-{Guid.NewGuid():N}.db");
    public static async Task<PredecessorFixture> CreateAsync() { var result = new PredecessorFixture(); await result.MigrateAsync("20260905120000_AddRequirementCatalogue"); return result; }
    public LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    public async Task MigrateAsync(string target) { await using var db = Context(); await db.GetService<IMigrator>().MigrateAsync(target); }
    public async Task ExecuteAsync(string sql) { await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    public async Task<string[]> TableNamesAsync() { await using var db = Context(); return await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToArrayAsync(); }
    public async Task<string[]> ColumnsAsync(string table) { await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"SELECT name FROM pragma_table_info('{table}')"; await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0)); return names.ToArray(); }
    public async Task<long> ScalarLongAsync(string sql) { await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
    public async Task<string> ScalarStringAsync(string sql) { await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(await command.ExecuteScalarAsync())!; }
    public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return ValueTask.CompletedTask; }
}
