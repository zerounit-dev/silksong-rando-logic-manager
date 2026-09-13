using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for ordinary V2 room-header field diffs.</summary>
public sealed class RoomEditorV2HeaderCommandTests
{
    [Fact]
    public async Task ContributorsMigration_UpgradesExistingRoomWithDefaultNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-contributors-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var legacy = new LogicDbContext(options))
            {
                await legacy.Database.MigrateAsync("20260809060221_DefaultNewVerificationToUnknown");
                var id = Guid.NewGuid(); var now = DateTime.UtcNow;
                await legacy.Database.ExecuteSqlAsync($"INSERT INTO Rooms (Id, FriendlyName, ReferenceId, SortOrder, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({id}, {"Room"}, {"room"}, 0, 0, {now}, {now});");
                await legacy.Database.MigrateAsync();
            }
            await using var verify = new LogicDbContext(options);
            Assert.Null((await verify.Rooms.SingleAsync()).Contributors);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Contributors_DefaultNullCreateUpdateNoOpAndEmptyStringRemainDistinct()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var room = await SeedAsync(fixture); var commands = Commands(fixture);
        Assert.Null(room.Contributors);
        var baseline = Baseline(room);
        Assert.Equal(V2RoomHeaderCommandStatus.Committed, (await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, contributors: "Alice"))).Status);
        await using (var db = fixture.CreateDbContext()) { room = await db.Rooms.SingleAsync(x => x.Id == room.Id); Assert.Equal("Alice", room.Contributors); baseline = Baseline(room); }
        Assert.Equal(V2RoomHeaderCommandStatus.Committed, (await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, contributors: ""))).Status);
        await using (var db = fixture.CreateDbContext()) { room = await db.Rooms.SingleAsync(x => x.Id == room.Id); Assert.Equal("", room.Contributors); baseline = Baseline(room); }
        Assert.Equal(V2RoomHeaderCommandStatus.Unchanged, (await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, contributors: ""))).Status);
        await using var unchanged = fixture.CreateDbContext(); Assert.Equal(baseline.UpdatedUtc, (await unchanged.Rooms.SingleAsync(x => x.Id == room.Id)).UpdatedUtc);
    }

    [Fact]
    public async Task Save_MergesDisjointChangesConflictsOnOverlapReportsMissingAndRetainsContributorsAcrossArchiveRestore()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var room = await SeedAsync(fixture); var commands = Commands(fixture); var baseline = Baseline(room);
        await using (var external = fixture.CreateDbContext()) { var row = await external.Rooms.SingleAsync(x => x.Id == room.Id); row.Comments = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2RoomHeaderCommandStatus.Committed, (await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, name: "local"))).Status);
        await using (var db = fixture.CreateDbContext()) { room = await db.Rooms.SingleAsync(x => x.Id == room.Id); Assert.Equal(("local", "external"), (room.FriendlyName, room.Comments)); baseline = Baseline(room); }
        await using (var external = fixture.CreateDbContext()) { var row = await external.Rooms.SingleAsync(x => x.Id == room.Id); row.Contributors = "external contributors"; await external.SaveChangesAsync(); }
        var conflict = await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, contributors: "local contributors"));
        Assert.Equal(V2RoomHeaderCommandStatus.Conflict, conflict.Status); Assert.Equal("external contributors", conflict.FreshBaseline!.Contributors); Assert.Equal("local contributors", conflict.RetainedDraft!.Contributors);
        await using (var lifecycle = fixture.CreateDbContext()) { var row = await lifecycle.Rooms.SingleAsync(x => x.Id == room.Id); row.IsArchived = true; row.ArchivedUtc = DateTime.UtcNow; await lifecycle.SaveChangesAsync(); row.IsArchived = false; row.ArchivedUtc = null; await lifecycle.SaveChangesAsync(); Assert.Equal("external contributors", row.Contributors); }
        await using (var delete = fixture.CreateDbContext()) { delete.Remove(await delete.Rooms.SingleAsync(x => x.Id == room.Id)); await delete.SaveChangesAsync(); }
        Assert.Equal(V2RoomHeaderCommandStatus.Missing, (await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, name: "missing"))).Status);
    }

    [Fact]
    public async Task Save_GuardsTheComparedVersionSoAnInterveningSameFieldWriteCannotBeOverwritten()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = await SeedAsync(fixture); var baseline = Baseline(room);
        var gate = new InterveningHeaderWrite(fixture, room.Id);
        var factory = new InterceptingFactory(fixture.DatabasePath, gate);
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);

        var result = await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, name: "local"));

        Assert.True(result.Status == V2RoomHeaderCommandStatus.Conflict, result.Message);
        Assert.Equal("intervening", result.FreshBaseline!.FriendlyName);
        Assert.Equal("local", result.RetainedDraft!.FriendlyName);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("intervening", (await verify.Rooms.SingleAsync(x => x.Id == room.Id)).FriendlyName);
    }

    [Fact]
    public async Task Save_GuardedUpdateReportsMissingWhenTheRoomIsDeletedAfterTheVersionRead()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = await SeedAsync(fixture); var baseline = Baseline(room);
        var factory = new InterceptingFactory(fixture.DatabasePath, new InterveningRoomDelete(fixture, room.Id));
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);

        var result = await commands.SaveRoomHeaderAsync(room.Id, baseline, Draft(room, name: "local"));

        Assert.Equal(V2RoomHeaderCommandStatus.Missing, result.Status);
        Assert.Null(result.FreshBaseline);
        Assert.Equal("local", result.RetainedDraft!.FriendlyName);
        await using var verify = fixture.CreateDbContext();
        Assert.Null(await verify.Rooms.SingleOrDefaultAsync(x => x.Id == room.Id));
    }

    [Fact]
    public async Task RoomReferenceProposal_CapturesOnlyResolvedActiveTargetsAndUpdatesCapturedMapScenesAtomically()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = await SeedAsync(fixture); var destination = new Room { FriendlyName = "Destination", ReferenceId = "destination" };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Add(destination);
            seed.AddRange(
                new RoomTransition { RoomId = destination.Id, FriendlyName = "active", Alias = "a", DestinationRoomReferenceText = "room", ResolvedDestinationRoomId = source.Id },
                new RoomTransition { RoomId = destination.Id, FriendlyName = "archived", Alias = "b", DestinationRoomReferenceText = "room", ResolvedDestinationRoomId = source.Id, IsArchived = true, ArchivedUtc = DateTime.UtcNow },
                new RoomTransition { RoomId = destination.Id, FriendlyName = "stale-text", Alias = "c", DestinationRoomReferenceText = "room", ResolvedDestinationRoomId = destination.Id });
            var map = new Map { InGameId = "map" }; seed.Add(map); await seed.SaveChangesAsync(); var zone = new MapZone { MapId = map.Id, InGameId = "zone" };
            seed.Add(zone); await seed.SaveChangesAsync();
            seed.AddRange(new MapScene { MapZoneId = zone.Id, InGameId = "captured", RoomReferenceText = "room", ResolvedRoomId = source.Id }, new MapScene { MapZoneId = zone.Id, InGameId = "stale", RoomReferenceText = "room", ResolvedRoomId = destination.Id });
            await seed.SaveChangesAsync();
        }
        var commands = Commands(fixture); var baseline = Baseline(source) with { ReferenceId = source.ReferenceId };
        var draft = Draft(source) with { ReferenceId = "renamed" };
        var prepared = await commands.PrepareRoomReferenceRenameAsync(source.Id, baseline, draft);
        Assert.Equal(V2RoomHeaderCommandStatus.Proposal, prepared.Status); Assert.Single(prepared.Proposal!.TransitionTargets); Assert.Single(prepared.Proposal.MapSceneTargets);
        var committed = await commands.ApplyRoomReferenceRenameAsync(source.Id, prepared.Proposal, true);
        Assert.Equal(V2RoomHeaderCommandStatus.Committed, committed.Status);
        Assert.True(committed.MapResolutionChanged); // the deliberately stale map reference loses its prior resolution.
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("renamed", (await verify.Rooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
        Assert.Equal("renamed", (await verify.RoomTransitions.SingleAsync(x => x.FriendlyName == "active")).DestinationRoomReferenceText);
        Assert.Equal("room", (await verify.RoomTransitions.SingleAsync(x => x.FriendlyName == "archived")).DestinationRoomReferenceText);
        Assert.Equal("room", (await verify.RoomTransitions.SingleAsync(x => x.FriendlyName == "stale-text")).DestinationRoomReferenceText);
        Assert.Equal("renamed", (await verify.MapScenes.SingleAsync(x => x.InGameId == "captured")).RoomReferenceText);
        Assert.Equal("room", (await verify.MapScenes.SingleAsync(x => x.InGameId == "stale")).RoomReferenceText);
    }

    [Fact]
    public async Task RoomReferenceProposal_DoNotUpdateRevertAndCapturedTargetConflictWriteNothing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var source = await SeedAsync(fixture); var other = new Room { FriendlyName = "Other", ReferenceId = "other" };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Add(other); seed.Add(new RoomTransition { RoomId = other.Id, FriendlyName = "target", Alias = "a", DestinationRoomReferenceText = "room", ResolvedDestinationRoomId = source.Id });
            var map = new Map { InGameId = "map" }; seed.Add(map); await seed.SaveChangesAsync();
            var zone = new MapZone { MapId = map.Id, InGameId = "zone" }; seed.Add(zone); await seed.SaveChangesAsync();
            seed.Add(new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = "room", ResolvedRoomId = source.Id }); await seed.SaveChangesAsync();
        }
        var commands = Commands(fixture); var baseline = Baseline(source) with { ReferenceId = "room" }; var draft = Draft(source) with { ReferenceId = "renamed" };
        var proposal = (await commands.PrepareRoomReferenceRenameAsync(source.Id, baseline, draft)).Proposal!;
        var reverted = commands.RevertRoomReferenceRename(proposal); Assert.Equal("room", reverted.RetainedDraft!.ReferenceId);
        var committed = await commands.ApplyRoomReferenceRenameAsync(source.Id, proposal, false);
        Assert.Equal(V2RoomHeaderCommandStatus.Committed, committed.Status);
        Assert.True(committed.MapResolutionChanged); // retained authored map text now resolves differently.
        await using (var check = fixture.CreateDbContext()) { Assert.Equal("renamed", (await check.Rooms.SingleAsync(x => x.Id == source.Id)).ReferenceId); var unchangedTarget = await check.RoomTransitions.SingleAsync(); Assert.Equal("room", unchangedTarget.DestinationRoomReferenceText); Assert.Null(unchangedTarget.ResolvedDestinationRoomId); source = await check.Rooms.SingleAsync(x => x.Id == source.Id); }
        await using (var restoreResolution = fixture.CreateDbContext()) { var target = await restoreResolution.RoomTransitions.SingleAsync(); target.DestinationRoomReferenceText = "renamed"; target.ResolvedDestinationRoomId = source.Id; await restoreResolution.SaveChangesAsync(); }
        baseline = Baseline(source) with { ReferenceId = "renamed" }; draft = Draft(source) with { ReferenceId = "again" }; proposal = (await commands.PrepareRoomReferenceRenameAsync(source.Id, baseline, draft)).Proposal!;
        await using (var external = fixture.CreateDbContext()) { var target = await external.RoomTransitions.SingleAsync(); target.DestinationRoomReferenceText = "external"; await external.SaveChangesAsync(); }
        var conflict = await commands.ApplyRoomReferenceRenameAsync(source.Id, proposal, true);
        Assert.Equal(V2RoomHeaderCommandStatus.Conflict, conflict.Status); Assert.Equal("again", conflict.RetainedDraft!.ReferenceId);
        await using var verify = fixture.CreateDbContext(); Assert.Equal("renamed", (await verify.Rooms.SingleAsync(x => x.Id == source.Id)).ReferenceId); Assert.Equal("external", (await verify.RoomTransitions.SingleAsync()).DestinationRoomReferenceText);
    }

    [Fact]
    public async Task RoomReferenceProposal_PostCommitResolverRetainsAuthoredTextAndUpdatesOnlyResolverMetadata()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = await SeedAsync(fixture); var other = new Room { FriendlyName = "Other", ReferenceId = "other" };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Add(other); seed.Add(new RoomTransition { RoomId = other.Id, FriendlyName = "target", Alias = "a", DestinationRoomReferenceText = "room", ResolvedDestinationRoomId = source.Id });
            var map = new Map { InGameId = "map" }; seed.Add(map); await seed.SaveChangesAsync();
            var zone = new MapZone { MapId = map.Id, InGameId = "zone" }; seed.Add(zone); await seed.SaveChangesAsync();
            seed.Add(new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = "room", ResolvedRoomId = source.Id });
            await seed.SaveChangesAsync();
        }

        var commands = Commands(fixture); var proposal = (await commands.PrepareRoomReferenceRenameAsync(source.Id, Baseline(source), Draft(source) with { ReferenceId = "renamed" })).Proposal!;
        var committed = await commands.ApplyRoomReferenceRenameAsync(source.Id, proposal, true);
        Assert.Equal(V2RoomHeaderCommandStatus.Committed, committed.Status);
        Assert.False(committed.MapResolutionChanged); // the captured map reference moved with the source and remains linked.

        await using var verify = fixture.CreateDbContext();
        var transition = await verify.RoomTransitions.SingleAsync(); var scene = await verify.MapScenes.SingleAsync();
        Assert.Equal(("renamed", source.Id), (transition.DestinationRoomReferenceText, transition.ResolvedDestinationRoomId));
        Assert.Equal(("renamed", source.Id), (scene.RoomReferenceText, scene.ResolvedRoomId));
        Assert.Equal("renamed", (await verify.Rooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
    }

    [Fact]
    public async Task RoomReferenceProposal_CapturedMapSceneConflictRollsBackSourceAndEveryCapturedTarget()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = await SeedAsync(fixture); var other = new Room { FriendlyName = "Other", ReferenceId = "other" };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Add(other); seed.Add(new RoomTransition { RoomId = other.Id, FriendlyName = "target", Alias = "a", DestinationRoomReferenceText = "room", ResolvedDestinationRoomId = source.Id });
            var map = new Map { InGameId = "map" }; seed.Add(map); await seed.SaveChangesAsync();
            var zone = new MapZone { MapId = map.Id, InGameId = "zone" }; seed.Add(zone); await seed.SaveChangesAsync();
            seed.Add(new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = "room", ResolvedRoomId = source.Id });
            await seed.SaveChangesAsync();
        }

        var commands = Commands(fixture); var proposal = (await commands.PrepareRoomReferenceRenameAsync(source.Id, Baseline(source), Draft(source) with { ReferenceId = "renamed" })).Proposal!;
        await using (var external = fixture.CreateDbContext()) { (await external.MapScenes.SingleAsync()).RoomReferenceText = "external"; await external.SaveChangesAsync(); }
        var conflict = await commands.ApplyRoomReferenceRenameAsync(source.Id, proposal, true);

        Assert.Equal(V2RoomHeaderCommandStatus.Conflict, conflict.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("room", (await verify.Rooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
        Assert.Equal("room", (await verify.RoomTransitions.SingleAsync()).DestinationRoomReferenceText);
        Assert.Equal("external", (await verify.MapScenes.SingleAsync()).RoomReferenceText);
    }

    private static RoomEditorV2CommandService Commands(MigratedSqliteFixture fixture) => new(new LogicCatalogService(fixture), fixture);
    private static RoomHeaderDurableBaseline Baseline(Room x) => new(x.Id, x.UpdatedUtc, x.FriendlyName, x.InGameId, x.Contributors, x.Comments);
    private static RoomHeaderDraft Draft(Room x, string? name = null, string? game = null, string? contributors = null, string? comments = null) => new(name ?? x.FriendlyName, game ?? x.InGameId, contributors ?? x.Contributors, comments ?? x.Comments);
    private static async Task<Room> SeedAsync(MigratedSqliteFixture fixture) { var room = new Room { FriendlyName = "Room", ReferenceId = "room", InGameId = "game", Comments = "comments" }; await using var db = fixture.CreateDbContext(); db.Add(room); await db.SaveChangesAsync(); return room; }

    private sealed class InterceptingFactory(string path, DbCommandInterceptor interceptor) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").AddInterceptors(interceptor).Options);
    }

    private sealed class InterveningHeaderWrite(MigratedSqliteFixture fixture, Guid roomId) : DbCommandInterceptor
    {
        private bool applied;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!applied && command.CommandText.StartsWith("UPDATE [Rooms]", StringComparison.Ordinal))
            {
                applied = true;
                await using var external = fixture.CreateDbContext();
                var row = await external.Rooms.SingleAsync(x => x.Id == roomId, cancellationToken);
                row.FriendlyName = "intervening";
                await external.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class InterveningRoomDelete(MigratedSqliteFixture fixture, Guid roomId) : DbCommandInterceptor
    {
        private bool applied;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!applied && command.CommandText.StartsWith("UPDATE [Rooms]", StringComparison.Ordinal))
            {
                applied = true;
                await using var external = fixture.CreateDbContext();
                external.Remove(await external.Rooms.SingleAsync(x => x.Id == roomId, cancellationToken));
                await external.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}
