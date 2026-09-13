using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite proof that the typed V2 command preserves the settled alias-group service semantics.</summary>
public sealed class RoomEditorV2ConnectionAnnotationPlacementCommandTests
{
    [Fact]
    public async Task Placement_AtomicallySynchronizesOneWayBidirectionalAndUnresolvedGroups()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        SubroomConnection Row(string alias, string source, string destination, int order) => new()
        {
            RoomId = room.Id, Alias = alias, FriendlyName = $"{alias} path", SourceSubroomReferenceText = source,
            DestinationSubroomReferenceText = destination, Requirements = "authored requirement", Notes = "authored note", SortOrder = order
        };
        var oneWay = Row("o", "a", "b", 0);
        var forward = Row("b", "a", "b", 1); var reverse = Row("b", "b", "a", 2);
        var unresolved = Row("u", "missing-a", "missing-b", 3);
        var archived = Row("z", "a", "b", 4); archived.IsArchived = true; archived.EnableAnnotation = false;
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, a, b, oneWay, forward, reverse, unresolved, archived); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }

        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.PlaceConnectionAnnotationAsync(room.Id, oneWay.Id, 10.25, 20.5)).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.PlaceConnectionAnnotationAsync(room.Id, reverse.Id, 30.25, 40.5)).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.PlaceConnectionAnnotationAsync(room.Id, unresolved.Id, 50.25, 60.5)).Status);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.SubroomConnections.OrderBy(x => x.SortOrder).ToArrayAsync();
        Assert.All(rows.Where(x => x.Alias is "o" or "b" or "u"), x => Assert.True(x.EnableAnnotation));
        Assert.Equal((30.25d, 40.5d), (rows.Single(x => x.Id == forward.Id).SceneUnitX, rows.Single(x => x.Id == forward.Id).SceneUnitY));
        Assert.Equal((30.25d, 40.5d), (rows.Single(x => x.Id == reverse.Id).SceneUnitX, rows.Single(x => x.Id == reverse.Id).SceneUnitY));
        Assert.Equal(("authored requirement", "authored note", "a", "b"), (rows.Single(x => x.Id == oneWay.Id).Requirements, rows.Single(x => x.Id == oneWay.Id).Notes, rows.Single(x => x.Id == oneWay.Id).SourceSubroomReferenceText, rows.Single(x => x.Id == oneWay.Id).DestinationSubroomReferenceText));
        Assert.False(rows.Single(x => x.Id == archived.Id).EnableAnnotation);

        var forwardUpdatedUtc = rows.Single(x => x.Id == forward.Id).UpdatedUtc;
        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, (await commands.PlaceConnectionAnnotationAsync(room.Id, forward.Id, 99, 100)).Status);
        await using var rejected = fixture.CreateDbContext();
        var unchanged = await rejected.SubroomConnections.SingleAsync(x => x.Id == forward.Id);
        Assert.Equal((30.25d, 40.5d), (unchanged.SceneUnitX, unchanged.SceneUnitY));
        Assert.Equal(forwardUpdatedUtc, unchanged.UpdatedUtc);
    }

    [Fact]
    public async Task Placement_WrongSuppliedRoomIsMissingAndDoesNotMutateEitherRoom()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var owner = new Room { FriendlyName = "owner", ReferenceId = "owner" };
        var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var source = new Subroom { RoomId = owner.Id, FriendlyName = "source", ReferenceId = "source" };
        var destination = new Subroom { RoomId = owner.Id, FriendlyName = "destination", ReferenceId = "destination" };
        var connection = new SubroomConnection { RoomId = owner.Id, Alias = "a", FriendlyName = "path", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(owner, other, source, destination, connection); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }

        var result = await new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture)
            .PlaceConnectionAnnotationAsync(other.Id, connection.Id, 10, 20);

        Assert.Equal(V2ConnectionCommandStatus.Missing, result.Status);
        await using var verify = fixture.CreateDbContext();
        var saved = await verify.SubroomConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.True(saved.EnableAnnotation); Assert.Null(saved.SceneUnitX); Assert.Null(saved.SceneUnitY);
        Assert.Equal(connection.UpdatedUtc, saved.UpdatedUtc);
    }

    [Fact]
    public async Task Disable_AtomicallyClearsEnabledSameRoomAliasGroupAndRetainsCoordinates()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var forward = new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = true, SceneUnitX = 12.5, SceneUnitY = 25.5 };
        var reverse = new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "path", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", Requirements = "r", SortOrder = 1, EnableAnnotation = true, SceneUnitX = 12.5, SceneUnitY = 25.5 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, other, a, b, forward, reverse); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }

        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        Assert.Equal(V2ConnectionCommandStatus.Missing, (await commands.DisableConnectionAnnotationAsync(other.Id, forward.Id)).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.DisableConnectionAnnotationAsync(room.Id, reverse.Id)).Status);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.SubroomConnections.Where(x => x.RoomId == room.Id).OrderBy(x => x.SortOrder).ToArrayAsync();
        Assert.All(rows, row => { Assert.False(row.EnableAnnotation); Assert.Equal((12.5d, 25.5d), (row.SceneUnitX, row.SceneUnitY)); });
        var timestamps = rows.Select(x => x.UpdatedUtc).ToArray();
        Assert.Equal(V2ConnectionCommandStatus.Unchanged, (await commands.DisableConnectionAnnotationAsync(room.Id, forward.Id)).Status);
        await using var rejected = fixture.CreateDbContext();
        Assert.Equal(timestamps, (await rejected.SubroomConnections.Where(x => x.RoomId == room.Id).OrderBy(x => x.SortOrder).Select(x => x.UpdatedUtc).ToArrayAsync()));
    }

    [Fact]
    public async Task Disable_IneligibleAliasGroupWritesNothing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var invalid = new SubroomConnection { RoomId = room.Id, Alias = "long", FriendlyName = "path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = true, SceneUnitX = 4, SceneUnitY = 8 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, a, b, invalid); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }

        var result = await new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture).DisableConnectionAnnotationAsync(room.Id, invalid.Id);
        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, result.Status);
        await using var verify = fixture.CreateDbContext();
        var saved = await verify.SubroomConnections.SingleAsync(x => x.Id == invalid.Id);
        Assert.True(saved.EnableAnnotation); Assert.Equal((4d, 8d), (saved.SceneUnitX, saved.SceneUnitY)); Assert.Equal(invalid.UpdatedUtc, saved.UpdatedUtc);
    }

    [Theory]
    [InlineData("valid", V2ConnectionCommandStatus.Committed)]
    [InlineData("malformed", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("disabled", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("archived", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("null", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("partial", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("nonfinite", V2ConnectionCommandStatus.ExpectedFailure)]
    [InlineData("wrong-room", V2ConnectionCommandStatus.Missing)]
    [InlineData("missing", V2ConnectionCommandStatus.Missing)]
    public async Task Move_AdmitsOnlyCurrentRenderedFullyEnabledAliasGroupAtomically(string state, V2ConnectionCommandStatus expected)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" }; var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" }; var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var first = new SubroomConnection { RoomId = room.Id, Alias = state == "malformed" ? "long" : "p", FriendlyName = "path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = state is not "disabled" and not "archived", SceneUnitX = 1, SceneUnitY = 2 };
        var second = new SubroomConnection { RoomId = room.Id, Alias = first.Alias, FriendlyName = "path", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", Requirements = "r", SortOrder = 1, EnableAnnotation = state is not "disabled" and not "archived", SceneUnitX = 1, SceneUnitY = 2 };
        if (state == "archived") first.IsArchived = true;
        if (state == "null") { first.SceneUnitX = null; first.SceneUnitY = null; }
        if (state == "partial") second.SceneUnitY = null;
        if (state == "nonfinite") second.SceneUnitX = double.PositiveInfinity;
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, other, a, b, first, second); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }
        var original = new Dictionary<Guid, (double? X, double? Y, DateTime UpdatedUtc, string Alias, string FriendlyName, string Source, string Destination, string Requirements, string Notes)> {
            [first.Id] = (first.SceneUnitX, first.SceneUnitY, first.UpdatedUtc, first.Alias, first.FriendlyName, first.SourceSubroomReferenceText, first.DestinationSubroomReferenceText, first.Requirements, first.Notes),
            [second.Id] = (second.SceneUnitX, second.SceneUnitY, second.UpdatedUtc, second.Alias, second.FriendlyName, second.SourceSubroomReferenceText, second.DestinationSubroomReferenceText, second.Requirements, second.Notes)
        };
        var id = state == "missing" ? Guid.NewGuid() : first.Id;
        var suppliedRoom = state == "wrong-room" ? other.Id : room.Id;
        var result = await new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture).MoveConnectionAnnotationAsync(suppliedRoom, id, 20, 21);
        Assert.Equal(expected, result.Status);
        await using var verify = fixture.CreateDbContext();
        var rows = await verify.SubroomConnections.OrderBy(x => x.SortOrder).ToArrayAsync();
        if (expected == V2ConnectionCommandStatus.Committed)
            Assert.All(rows, row => Assert.Equal((20d, 21d), (row.SceneUnitX, row.SceneUnitY)));
        else
            Assert.Equal(first.SceneUnitX, rows.Single(row => row.Id == first.Id).SceneUnitX);
        if (expected != V2ConnectionCommandStatus.Committed)
        {
            Assert.All(rows, row =>
            {
                var before = original[row.Id];
                Assert.Equal(before.X, row.SceneUnitX); Assert.Equal(before.Y, row.SceneUnitY); Assert.Equal(before.UpdatedUtc, row.UpdatedUtc);
                Assert.Equal(before.Alias, row.Alias); Assert.Equal(before.FriendlyName, row.FriendlyName);
                Assert.Equal(before.Source, row.SourceSubroomReferenceText); Assert.Equal(before.Destination, row.DestinationSubroomReferenceText);
                Assert.Equal(before.Requirements, row.Requirements); Assert.Equal(before.Notes, row.Notes);
            });
        }
    }
}
