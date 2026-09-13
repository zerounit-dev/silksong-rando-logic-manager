using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for the typed V2 connection adapter.</summary>
public sealed class RoomEditorV2ConnectionCommandTests
{
    [Fact]
    public async Task Save_ActiveCurrentRoomConnectionCommitsWhileWrongRoomRemainsMissing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, row) = await SeedAsync(fixture);
        var otherRoom = new Room { FriendlyName = "other", ReferenceId = "other", SortOrder = 1 };
        await using (var seed = fixture.CreateDbContext()) { seed.Add(otherRoom); await seed.SaveChangesAsync(); }

        var commands = Commands(fixture);
        var saved = await commands.SaveConnectionAsync(room.Id, Baseline(row), Draft("a", "updated", "unresolved-source", "unresolved-destination", "old", "updated note", false, null, null, false, null));

        Assert.Equal(V2ConnectionCommandStatus.Committed, saved.Status);
        await using (var verify = fixture.CreateDbContext())
        {
            var durable = await verify.SubroomConnections.SingleAsync(x => x.Id == row.Id);
            Assert.Equal(("updated", "updated note"), (durable.FriendlyName, durable.Notes));
            var updatedUtc = durable.UpdatedUtc;
            var missing = await commands.SaveConnectionAsync(otherRoom.Id, Baseline(durable), Draft("a", "wrong room", "unresolved-source", "unresolved-destination", "old", "updated note", false, null, null, false, null));
            Assert.Equal(V2ConnectionCommandStatus.Missing, missing.Status);
            verify.ChangeTracker.Clear();
            Assert.Equal(updatedUtc, (await verify.SubroomConnections.SingleAsync(x => x.Id == row.Id)).UpdatedUtc);
        }
    }

    [Fact]
    public async Task Save_DiffsEveryEditableScalar_NoOpsAndMergesOrConflicts()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, row) = await SeedAsync(fixture, resolvedEndpoints: false, withAnnotation: true); var commands = Commands(fixture); var baseline = Baseline(row);
        var draft = Draft("a", "new", "source", "destination", "requirements", "notes", false, 4, 5, true, true);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.SaveConnectionAsync(room.Id, baseline, draft)).Status);
        await using (var verify = fixture.CreateDbContext()) { var saved = await verify.SubroomConnections.SingleAsync(x => x.Id == row.Id); var endpoints = await verify.Subrooms.Where(x => x.RoomId == room.Id).ToDictionaryAsync(x => x.ReferenceId, x => x.Id); Assert.Equal(("a", "new", "source", "destination", "requirements", "notes", true, 1d, 2d, true, true, endpoints["source"], endpoints["destination"]), (saved.Alias, saved.FriendlyName, saved.SourceSubroomReferenceText, saved.DestinationSubroomReferenceText, saved.Requirements, saved.Notes, saved.EnableAnnotation, saved.SceneUnitX, saved.SceneUnitY, saved.IsTodo, saved.IsVerified, saved.ResolvedSourceSubroomId, saved.ResolvedDestinationSubroomId)); baseline = Baseline(saved); }
        Assert.Equal(V2ConnectionCommandStatus.Unchanged, (await commands.SaveConnectionAsync(room.Id, baseline, draft)).Status);
        await using (var verify = fixture.CreateDbContext()) Assert.Equal(baseline.UpdatedUtc, (await verify.SubroomConnections.SingleAsync(x => x.Id == row.Id)).UpdatedUtc);
        await using (var external = fixture.CreateDbContext()) { var saved = await external.SubroomConnections.SingleAsync(x => x.Id == row.Id); saved.Notes = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.SaveConnectionAsync(room.Id, baseline, Draft("a", "merged", "source", "destination", "requirements", "notes", false, 4, 5, true, true))).Status);
        await using (var read = fixture.CreateDbContext()) { var saved = await read.SubroomConnections.SingleAsync(x => x.Id == row.Id); Assert.Equal(("merged", "external"), (saved.FriendlyName, saved.Notes)); baseline = Baseline(saved); }
        await using (var external = fixture.CreateDbContext()) { var saved = await external.SubroomConnections.SingleAsync(x => x.Id == row.Id); saved.FriendlyName = "external name"; await external.SaveChangesAsync(); }
        var conflict = await commands.SaveConnectionAsync(room.Id, baseline, Draft("a", "local name", "source", "destination", "requirements", "external", false, 4, 5, true, true));
        Assert.Equal(V2ConnectionCommandStatus.Conflict, conflict.Status); Assert.Equal("external name", conflict.FreshBaseline!.FriendlyName); Assert.Equal("local name", conflict.RetainedDraft!.FriendlyName);
        await using (var deleted = fixture.CreateDbContext()) { deleted.Remove(await deleted.SubroomConnections.SingleAsync(x => x.Id == row.Id)); await deleted.SaveChangesAsync(); }
        Assert.Equal(V2ConnectionCommandStatus.Missing, (await commands.SaveConnectionAsync(room.Id, baseline, draft)).Status);
    }

    [Fact]
    public async Task CreateReorderLifecycleAndDelete_RespectActiveAndArchivedPartitions()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, first) = await SeedAsync(fixture); var second = new SubroomConnection { RoomId = room.Id, Alias = "b", FriendlyName = "second", Requirements = "r", SortOrder = 1 };
        await using (var seed = fixture.CreateDbContext()) { seed.Add(second); await seed.SaveChangesAsync(); }
        var commands = Commands(fixture); var created = await commands.CreateConnectionAsync(room.Id, Draft("c", "created", "", "", "requirement", "note", true, 4, 5, true, null));
        Assert.Equal(V2ConnectionCommandStatus.Committed, created.Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.ReorderConnectionAsync(room.Id, second.Id, 0)).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.SetConnectionArchiveAsync(room.Id, first.Id, true)).Status);
        await using (var archived = fixture.CreateDbContext()) { var saved = await archived.SubroomConnections.SingleAsync(x => x.Id == first.Id); Assert.Equal((first.ResolvedSourceSubroomId, first.ResolvedDestinationSubroomId), (saved.ResolvedSourceSubroomId, saved.ResolvedDestinationSubroomId)); }
        await using (var beforeRejectedReorder = fixture.CreateDbContext())
        {
            var archivedBefore = await beforeRejectedReorder.SubroomConnections.AsNoTracking().SingleAsync(x => x.Id == first.Id);
            var result = await commands.ReorderConnectionAsync(room.Id, first.Id, 0);
            Assert.Equal(V2ConnectionCommandStatus.Missing, result.Status);
            await using var afterRejectedReorder = fixture.CreateDbContext();
            var archivedAfter = await afterRejectedReorder.SubroomConnections.AsNoTracking().SingleAsync(x => x.Id == first.Id);
            Assert.Equal((archivedBefore.SortOrder, archivedBefore.UpdatedUtc), (archivedAfter.SortOrder, archivedAfter.UpdatedUtc));
        }
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.SetConnectionArchiveAsync(room.Id, first.Id, false)).Status);
        await using (var restored = fixture.CreateDbContext()) { var saved = await restored.SubroomConnections.SingleAsync(x => x.Id == first.Id); Assert.Equal((first.ResolvedSourceSubroomId, first.ResolvedDestinationSubroomId), (saved.ResolvedSourceSubroomId, saved.ResolvedDestinationSubroomId)); }
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.SetConnectionArchiveAsync(room.Id, first.Id, true)).Status);
        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, (await commands.DeleteConnectionAsync(room.Id, second.Id)).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await commands.DeleteConnectionAsync(room.Id, first.Id)).Status);
        await using var verify = fixture.CreateDbContext(); Assert.Null(await verify.SubroomConnections.SingleOrDefaultAsync(x => x.Id == first.Id)); var savedCreated = await verify.SubroomConnections.SingleAsync(x => x.Id == created.CreatedEntityId);
        Assert.Equal(("c", "created", "", "", "requirement", "note", true, (double?)null, (double?)null, true, (bool?)null), (savedCreated.Alias, savedCreated.FriendlyName, savedCreated.SourceSubroomReferenceText, savedCreated.DestinationSubroomReferenceText, savedCreated.Requirements, savedCreated.Notes, savedCreated.EnableAnnotation, savedCreated.SceneUnitX, savedCreated.SceneUnitY, savedCreated.IsTodo, savedCreated.IsVerified));
        Assert.Equal([second.Id, created.CreatedEntityId!.Value], await verify.SubroomConnections.Where(x => x.RoomId == room.Id && !x.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToArrayAsync());
    }

    [Fact]
    public async Task Save_UsesAtomicAliasGroupSynchronizationAndRoomSerialization()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var first = new SubroomConnection { RoomId = room.Id, Alias = "a", FriendlyName = "path", Requirements = "r", SortOrder = 0, EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2 };
        var second = new SubroomConnection { RoomId = room.Id, Alias = "a", FriendlyName = "path", Requirements = "r", SortOrder = 1, EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(room, first, second); await seed.SaveChangesAsync(); }
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); var coordinator = new ConnectionRoomSaveCoordinator(new LogicCatalogService(fixture), async () => { entered.TrySetResult(); await release.Task; });
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture, coordinator);
        var firstSave = commands.SaveConnectionAsync(room.Id, Baseline(first), Draft("b", "renamed", "", "", "r", "n", false, null, null, false, null));
        await entered.Task; var secondSave = commands.SaveConnectionAsync(room.Id, Baseline(second), Draft("a", "path", "", "", "r", "second", false, null, null, false, null));
        release.SetResult(); Assert.Equal(V2ConnectionCommandStatus.Committed, (await firstSave).Status); Assert.Equal(V2ConnectionCommandStatus.Committed, (await secondSave).Status);
        await using var verify = fixture.CreateDbContext(); var rows = await verify.SubroomConnections.OrderBy(x => x.SortOrder).ToArrayAsync();
        Assert.Equal((false, null, null), (rows[0].EnableAnnotation, rows[0].SceneUnitX, rows[0].SceneUnitY)); Assert.Equal((true, 1d, 2d, "second"), (rows[1].EnableAnnotation, rows[1].SceneUnitX, rows[1].SceneUnitY, rows[1].Notes));
    }

    [Fact]
    public async Task ScaffoldInverse_RequiresEligibleResolvedOneWayAndCreatesExpectedRow()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 }; var destination = new Subroom { RoomId = room.Id, FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
        var connection = new SubroomConnection { RoomId = room.Id, Alias = "a", FriendlyName = "path", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", Requirements = "r", Notes = "note", IsTodo = true, IsVerified = true, SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(room, source, destination, connection); await seed.SaveChangesAsync(); await new LogicReferenceResolver(seed).ResolveAsync(); }
        var commands = Commands(fixture); var result = await commands.ScaffoldInverseConnectionAsync(room.Id, connection.Id); Assert.Equal(V2ConnectionCommandStatus.Committed, result.Status);
        await using (var verify = fixture.CreateDbContext()) { var inverse = await verify.SubroomConnections.SingleAsync(x => x.Id == result.CreatedEntityId); Assert.Equal(("a", "path", "destination", "source", "", "", false, (bool?)null, 1, destination.Id, source.Id), (inverse.Alias, inverse.FriendlyName, inverse.SourceSubroomReferenceText, inverse.DestinationSubroomReferenceText, inverse.Requirements, inverse.Notes, inverse.IsTodo, inverse.IsVerified, inverse.SortOrder, inverse.ResolvedSourceSubroomId, inverse.ResolvedDestinationSubroomId)); }
        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, (await commands.ScaffoldInverseConnectionAsync(room.Id, connection.Id)).Status);
    }

    private static RoomEditorV2CommandService Commands(MigratedSqliteFixture fixture) => new(new LogicCatalogService(fixture), fixture);
    private static ConnectionDurableBaseline Baseline(SubroomConnection x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY, x.IsTodo, x.IsVerified);
    private static ConnectionDraft Draft(string alias, string name, string source, string destination, string requirements, string notes, bool annotation, double? x, double? y, bool todo, bool? verification) => new(Guid.NewGuid(), alias, name, source, destination, requirements, notes, annotation, x, y, todo, verification);
    private static async Task<(Room Room, SubroomConnection Connection)> SeedAsync(MigratedSqliteFixture fixture, bool resolvedEndpoints = true, bool withAnnotation = false) { var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 }; var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 }; var destination = new Subroom { RoomId = room.Id, FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 }; var connection = new SubroomConnection { RoomId = room.Id, Alias = "a", FriendlyName = "old", SourceSubroomReferenceText = resolvedEndpoints ? "source" : "unresolved-source", DestinationSubroomReferenceText = resolvedEndpoints ? "destination" : "unresolved-destination", Requirements = "old", Notes = "old", EnableAnnotation = withAnnotation, SceneUnitX = withAnnotation ? 1 : null, SceneUnitY = withAnnotation ? 2 : null, SortOrder = 0 }; await using var db = fixture.CreateDbContext(); db.AddRange(room, source, destination, connection); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); return (room, connection); }
}
