using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Separate migration-current SQLite command contexts prove stale scene moves never partially update an alias group.</summary>
public sealed class RoomEditorV2ConnectionAnnotationMoveRaceTests
{
    [Theory]
    [InlineData("disable", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("archive", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("alias", V2ConnectionCommandStatus.ExpectedFailure)]
    public async Task Move_AfterGatedCompetingStateChange_RejectsAtomicallyAndRetainsAuthoredFields(string race, V2ConnectionCommandStatus expected)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var competitor = Task.Run(async () =>
        {
            await release.Task;
            return race switch
            {
                "disable" => await commands.DisableConnectionAnnotationAsync(seed.Room.Id, seed.First.Id),
                "archive" => await commands.SetConnectionArchiveAsync(seed.Room.Id, seed.First.Id, true),
                _ => await commands.SaveConnectionAsync(seed.Room.Id, Baseline(seed.First), Draft(seed.First, "split"))
            };
        });
        var move = Task.Run(async () => { await release.Task; await competitor; return await commands.MoveConnectionAnnotationAsync(seed.Room.Id, seed.First.Id, 90, 91); });
        release.SetResult();
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await competitor).Status);
        Assert.Equal(expected, (await move).Status);
        await AssertUnchangedMoveGeometryAsync(fixture, seed, race);
    }

    [Fact]
    public async Task ConcurrentMoves_FromIndependentContexts_CommitWholeAliasGroupsWithoutPartialCoordinates()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        var first = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var second = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstMove = Task.Run(async () => { await gate.Task; return await first.MoveConnectionAnnotationAsync(seed.Room.Id, seed.First.Id, 30, 31); });
        var secondMove = Task.Run(async () => { await gate.Task; await firstMove; return await second.MoveConnectionAnnotationAsync(seed.Room.Id, seed.Second.Id, 40, 41); });
        gate.SetResult();
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await firstMove).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await secondMove).Status);
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.SubroomConnections.OrderBy(x => x.SortOrder).ToArrayAsync();
        Assert.All(rows, row => { Assert.Equal(40, row.SceneUnitX); Assert.Equal(41, row.SceneUnitY); Assert.Equal("path", row.FriendlyName); Assert.Equal("r", row.Requirements); Assert.Equal("n", row.Notes); });
    }

    private static async Task AssertUnchangedMoveGeometryAsync(MigratedSqliteFixture fixture, Seed seed, string race)
    {
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.SubroomConnections.OrderBy(x => x.SortOrder).ToArrayAsync();
        Assert.All(rows, row => { Assert.NotEqual(90, row.SceneUnitX); Assert.NotEqual(91, row.SceneUnitY); Assert.Equal("path", row.FriendlyName); Assert.Equal("r", row.Requirements); Assert.Equal("n", row.Notes); });
        if (race == "disable") Assert.All(rows, row => Assert.False(row.EnableAnnotation));
        if (race == "archive") Assert.True(rows.Single(x => x.Id == seed.First.Id).IsArchived);
        if (race == "alias") { Assert.Equal("split", rows.Single(x => x.Id == seed.First.Id).Alias); Assert.False(rows.Single(x => x.Id == seed.First.Id).EnableAnnotation); }
    }

    private static async Task<Seed> SeedAsync(MigratedSqliteFixture fixture)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "a", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "b", ReferenceId = "b" };
        var first = new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", Notes = "n", EnableAnnotation = true, SceneUnitX = 10, SceneUnitY = 11 };
        var second = new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "path", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", Requirements = "r", Notes = "n", SortOrder = 1, EnableAnnotation = true, SceneUnitX = 10, SceneUnitY = 11 };
        await using var db = fixture.CreateDbContext(); db.AddRange(room, a, b, first, second); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); return new(room, first, second);
    }
    private static ConnectionDurableBaseline Baseline(SubroomConnection row) => new(row.Id, row.UpdatedUtc, row.SortOrder, row.IsArchived, row.Alias, row.FriendlyName, row.SourceSubroomReferenceText, row.DestinationSubroomReferenceText, row.Requirements, row.Notes, row.EnableAnnotation, row.SceneUnitX, row.SceneUnitY, row.IsTodo, row.IsVerified);
    private static ConnectionDraft Draft(SubroomConnection row, string alias) => new(Guid.NewGuid(), alias, row.FriendlyName, row.SourceSubroomReferenceText, row.DestinationSubroomReferenceText, row.Requirements, row.Notes, row.EnableAnnotation, row.SceneUnitX, row.SceneUnitY, row.IsTodo, row.IsVerified);
    private sealed record Seed(Room Room, SubroomConnection First, SubroomConnection Second);
}
