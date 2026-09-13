using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for the Phase 2a typed adapter.</summary>
public sealed class RoomEditorV2SubroomCommandTests
{
    [Fact]
    public async Task Save_DiffsOnlyFriendlyNameAndNotesAtomically_AndNoOpDoesNotAdvanceTimestamp()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, subroom) = await SeedAsync(fixture);
        var commands = Commands(fixture); var baseline = Baseline(subroom);

        var committed = await commands.SaveSubroomAsync(room.Id, baseline, Draft("new name", "one", "new note"));
        Assert.Equal(V2SubroomCommandStatus.Committed, committed.Status);
        await using (var check = fixture.CreateDbContext())
        {
            var saved = await check.Subrooms.SingleAsync(x => x.Id == subroom.Id);
            Assert.Equal(("new name", "one", "new note"), (saved.FriendlyName, saved.ReferenceId, saved.Notes));
            Assert.True(saved.UpdatedUtc >= baseline.UpdatedUtc);
            baseline = Baseline(saved);
        }
        var unchanged = await commands.SaveSubroomAsync(room.Id, baseline, Draft("new name", "one", "new note"));
        Assert.Equal(V2SubroomCommandStatus.Unchanged, unchanged.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(baseline.UpdatedUtc, (await verify.Subrooms.SingleAsync(x => x.Id == subroom.Id)).UpdatedUtc);
    }

    [Fact]
    public async Task Save_MergesDisjointExternalEdit_AndConflictsOnSameField()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, subroom) = await SeedAsync(fixture); var commands = Commands(fixture); var baseline = Baseline(subroom);
        await using (var external = fixture.CreateDbContext()) { var row = await external.Subrooms.SingleAsync(x => x.Id == subroom.Id); row.Notes = "external"; await external.SaveChangesAsync(); }
        var merged = await commands.SaveSubroomAsync(room.Id, baseline, Draft("local", "one", "old"));
        Assert.Equal(V2SubroomCommandStatus.Committed, merged.Status);
        await using (var check = fixture.CreateDbContext()) { var row = await check.Subrooms.SingleAsync(x => x.Id == subroom.Id); Assert.Equal(("local", "external"), (row.FriendlyName, row.Notes)); baseline = Baseline(row); }
        await using (var external = fixture.CreateDbContext()) { var row = await external.Subrooms.SingleAsync(x => x.Id == subroom.Id); row.FriendlyName = "external name"; await external.SaveChangesAsync(); }
        var conflict = await commands.SaveSubroomAsync(room.Id, baseline, Draft("local again", "one", "external"));
        Assert.Equal(V2SubroomCommandStatus.Conflict, conflict.Status);
        Assert.Equal("external name", conflict.FreshBaseline!.FriendlyName);
        Assert.Equal("local again", conflict.RetainedDraft!.FriendlyName);
    }

    [Fact]
    public async Task Save_MissingAndUnavailableGeometryPersistenceIsAnApplicationError()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, subroom) = await SeedAsync(fixture); var commands = Commands(fixture); var baseline = Baseline(subroom);
        var geometry = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SaveSubroomAsync(room.Id, baseline, Draft("one", "one", "old", 1, 2, 3, 4)));
        Assert.Contains("geometry persistence", geometry.Message);
        await using (var unchanged = fixture.CreateDbContext())
        {
            var persisted = await unchanged.Subrooms.SingleAsync(x => x.Id == subroom.Id);
            Assert.Equal(("one", "old", baseline.UpdatedUtc), (persisted.ReferenceId, persisted.Notes, persisted.UpdatedUtc));
        }
        await using (var delete = fixture.CreateDbContext()) { delete.Remove(await delete.Subrooms.SingleAsync(x => x.Id == subroom.Id)); await delete.SaveChangesAsync(); }
        var missing = await commands.SaveSubroomAsync(room.Id, baseline, Draft("changed", "one", "old"));
        Assert.Equal(V2SubroomCommandStatus.Missing, missing.Status);
    }

    [Fact]
    public async Task Geometry_WritesCompleteOrNullAtomicallyAndRejectsConflicts()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, subroom) = await SeedAsync(fixture); var commands = Commands(fixture); var baseline = Baseline(subroom);

        var committed = await commands.SaveSubroomGeometryAsync(room.Id, baseline, 1, 2, 3, 4);
        Assert.Equal(V2SubroomCommandStatus.Committed, committed.Status);
        await using (var verify = fixture.CreateDbContext())
        {
            var saved = await verify.Subrooms.SingleAsync(x => x.Id == subroom.Id);
            Assert.Equal((1d, 2d, 3d, 4d), (saved.SceneUnitX, saved.SceneUnitY, saved.SceneUnitWidth, saved.SceneUnitHeight));
            baseline = Baseline(saved);
        }
        Assert.Equal(V2SubroomCommandStatus.ExpectedFailure, (await commands.SaveSubroomGeometryAsync(room.Id, baseline, 1, null, null, null)).Status);
        await using (var external = fixture.CreateDbContext()) { var row = await external.Subrooms.SingleAsync(x => x.Id == subroom.Id); row.SceneUnitWidth = 9; await external.SaveChangesAsync(); }
        Assert.Equal(V2SubroomCommandStatus.Conflict, (await commands.SaveSubroomGeometryAsync(room.Id, baseline, 5, 6, 7, 8)).Status);
        await using var final = fixture.CreateDbContext();
        var durable = await final.Subrooms.SingleAsync(x => x.Id == subroom.Id);
        Assert.Equal((1d, 2d, 9d, 4d), (durable.SceneUnitX, durable.SceneUnitY, durable.SceneUnitWidth, durable.SceneUnitHeight));
    }

    [Fact]
    public async Task SubroomReferenceProposal_CapturesExactActiveResolvedTargetsAndAppliesThreeOutcomesAtomically()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, source) = await SeedAsync(fixture);
        var otherRoom = new Room { FriendlyName = "Other", ReferenceId = "other" };
        var otherSubroom = new Subroom { RoomId = room.Id, FriendlyName = "other", ReferenceId = "other" };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.AddRange(otherRoom, otherSubroom); await seed.SaveChangesAsync();
            seed.AddRange(
                new RoomTransition { RoomId = room.Id, FriendlyName = "transition", Alias = "t", SourceSubroomReferenceText = "one", ResolvedSourceSubroomId = source.Id },
                new RoomTransition { RoomId = room.Id, FriendlyName = "archived transition", Alias = "ta", SourceSubroomReferenceText = "one", ResolvedSourceSubroomId = source.Id, IsArchived = true },
                new SubroomConnection { RoomId = room.Id, FriendlyName = "connection", Alias = "c", SourceSubroomReferenceText = "one", DestinationSubroomReferenceText = "one", ResolvedSourceSubroomId = source.Id, ResolvedDestinationSubroomId = source.Id },
                new SubroomConnection { RoomId = room.Id, FriendlyName = "stale", Alias = "s", SourceSubroomReferenceText = "one", DestinationSubroomReferenceText = "other", ResolvedSourceSubroomId = otherSubroom.Id, ResolvedDestinationSubroomId = otherSubroom.Id },
                new CheckLocation { RoomId = room.Id, FriendlyName = "check", SubroomReferenceText = "one", ResolvedSubroomId = source.Id },
                new CheckLocation { RoomId = room.Id, FriendlyName = "archived check", SubroomReferenceText = "one", ResolvedSubroomId = source.Id, IsArchived = true });
            await seed.SaveChangesAsync();
        }
        var commands = Commands(fixture); var proposal = (await commands.PrepareSubroomReferenceRenameAsync(room.Id, Baseline(source), Draft("one", "renamed", "old"))).Proposal!;
        Assert.Equal((1, 2, 1), (proposal.TransitionTargets.Count, proposal.ConnectionTargets.Count, proposal.CheckTargets.Count));
        Assert.Equal(V2SubroomCommandStatus.Committed, (await commands.ApplySubroomReferenceRenameAsync(room.Id, proposal, true)).Status);
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.Equal("renamed", (await verify.Subrooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
            Assert.All(await verify.RoomTransitions.Where(x => !x.IsArchived).ToListAsync(), x => Assert.Equal("renamed", x.SourceSubroomReferenceText));
            var connection = await verify.SubroomConnections.SingleAsync(x => x.FriendlyName == "connection"); Assert.Equal(("renamed", "renamed"), (connection.SourceSubroomReferenceText, connection.DestinationSubroomReferenceText));
            Assert.Equal("renamed", (await verify.CheckLocations.SingleAsync(x => x.FriendlyName == "check")).SubroomReferenceText);
        }
        await using (var reseed = fixture.CreateDbContext()) { source = await reseed.Subrooms.SingleAsync(x => x.Id == source.Id); }
        proposal = (await commands.PrepareSubroomReferenceRenameAsync(room.Id, Baseline(source), Draft("one", "again", "old"))).Proposal!;
        Assert.Equal(V2SubroomCommandStatus.Committed, (await commands.ApplySubroomReferenceRenameAsync(room.Id, proposal, false)).Status);
        await using var final = fixture.CreateDbContext(); Assert.Equal("again", (await final.Subrooms.SingleAsync(x => x.Id == source.Id)).ReferenceId); Assert.Equal("renamed", (await final.RoomTransitions.SingleAsync(x => x.FriendlyName == "transition")).SourceSubroomReferenceText);
    }

    [Fact]
    public async Task SubroomReferenceProposal_RevertAndStaleCapturedTargetConflictWriteNothing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, source) = await SeedAsync(fixture);
        var target = new RoomTransition { RoomId = room.Id, FriendlyName = "target", Alias = "t", SourceSubroomReferenceText = "one", ResolvedSourceSubroomId = source.Id };
        await using (var seed = fixture.CreateDbContext()) { seed.Add(target); await seed.SaveChangesAsync(); }
        var commands = Commands(fixture); var proposal = (await commands.PrepareSubroomReferenceRenameAsync(room.Id, Baseline(source), Draft("one", "renamed", "old"))).Proposal!;
        Assert.Equal("one", commands.RevertSubroomReferenceRename(proposal).RetainedDraft!.ReferenceId);
        await using (var external = fixture.CreateDbContext()) { var changed = await external.RoomTransitions.SingleAsync(x => x.Id == target.Id); changed.SourceSubroomReferenceText = "external"; await external.SaveChangesAsync(); }
        var conflict = await commands.ApplySubroomReferenceRenameAsync(room.Id, proposal, true);
        Assert.Equal(V2SubroomCommandStatus.Conflict, conflict.Status); Assert.Equal("renamed", conflict.RetainedDraft!.ReferenceId);
        await using var verify = fixture.CreateDbContext(); Assert.Equal("one", (await verify.Subrooms.SingleAsync(x => x.Id == source.Id)).ReferenceId); Assert.Equal("external", (await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).SourceSubroomReferenceText);
    }

    [Fact]
    public async Task CreateAndLifecycle_RespectRestrictionsPartitionsAndResolverCleanup()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, first) = await SeedAsync(fixture); var second = new Subroom { RoomId = room.Id, FriendlyName = "two", ReferenceId = "two", SortOrder = 1 };
        var transition = new RoomTransition { RoomId = room.Id, FriendlyName = "exit", Alias = "e", SourceSubroomReferenceText = "one", SortOrder = 0 };
        var connection = new SubroomConnection { RoomId = room.Id, FriendlyName = "path", Alias = "p", SourceSubroomReferenceText = "one", DestinationSubroomReferenceText = "two", SortOrder = 0 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", SubroomReferenceText = "one", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(second, transition, connection, check); await seed.SaveChangesAsync(); await new LogicReferenceResolver(seed).ResolveAsync(); }
        var commands = Commands(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.CreateSubroomAsync(room.Id, Draft("bad", "bad", "", 1, null, null, null)));
        var created = await commands.CreateSubroomAsync(room.Id, Draft("created", "created", "note")); Assert.Equal(V2SubroomCommandStatus.Committed, created.Status);
        await commands.ReorderSubroomAsync(room.Id, second.Id, 0);
        await commands.SetSubroomArchiveAsync(room.Id, first.Id, true);
        await commands.ReorderSubroomAsync(room.Id, first.Id, 0); // archived partition is independently reorderable
        var deleted = await commands.DeleteSubroomAsync(room.Id, first.Id); Assert.Equal(V2SubroomCommandStatus.Committed, deleted.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal([second.Id, created.CreatedEntityId!.Value], await verify.Subrooms.Where(x => x.RoomId == room.Id && !x.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToArrayAsync());
        Assert.Null(await verify.Subrooms.SingleOrDefaultAsync(x => x.Id == first.Id));
        var savedTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id); var savedConnection = await verify.SubroomConnections.SingleAsync(x => x.Id == connection.Id); var savedCheck = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
        Assert.Null(savedTransition.ResolvedSourceSubroomId); Assert.Null(savedConnection.ResolvedSourceSubroomId); Assert.Null(savedConnection.ResolvedDestinationSubroomId); Assert.Null(savedCheck.ResolvedSubroomId);
        Assert.Equal("one", savedTransition.SourceSubroomReferenceText); Assert.Equal("one", savedConnection.SourceSubroomReferenceText); Assert.Equal("one", savedCheck.SubroomReferenceText);
    }

    [Fact]
    public async Task Lifecycle_ArchivesRestoresAndPermanentlyDeletesOnlyArchivedSubrooms()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, subroom) = await SeedAsync(fixture); var commands = Commands(fixture);
        Assert.Equal(V2SubroomCommandStatus.Committed, (await commands.SetSubroomArchiveAsync(room.Id, subroom.Id, true)).Status);
        await using (var archived = fixture.CreateDbContext()) Assert.True((await archived.Subrooms.SingleAsync(x => x.Id == subroom.Id)).IsArchived);
        Assert.Equal(V2SubroomCommandStatus.Committed, (await commands.SetSubroomArchiveAsync(room.Id, subroom.Id, false)).Status);
        Assert.Equal(V2SubroomCommandStatus.ExpectedFailure, (await commands.DeleteSubroomAsync(room.Id, subroom.Id)).Status);
        Assert.Equal(V2SubroomCommandStatus.Committed, (await commands.SetSubroomArchiveAsync(room.Id, subroom.Id, true)).Status);
        Assert.Equal(V2SubroomCommandStatus.Committed, (await commands.DeleteSubroomAsync(room.Id, subroom.Id)).Status);
        await using var verify = fixture.CreateDbContext(); Assert.Null(await verify.Subrooms.SingleOrDefaultAsync(x => x.Id == subroom.Id));
    }

    private static RoomEditorV2CommandService Commands(MigratedSqliteFixture fixture) => new(new LogicCatalogService(fixture), fixture);
    private static SubroomDraft Draft(string name, string reference, string notes, double? x = null, double? y = null, double? width = null, double? height = null) => new(Guid.NewGuid(), name, reference, notes, x, y, width, height);
    private static SubroomDurableBaseline Baseline(Subroom row) => new(row.Id, row.UpdatedUtc, row.SortOrder, row.IsArchived, row.FriendlyName, row.ReferenceId, row.Notes ?? "", row.SceneUnitX, row.SceneUnitY, row.SceneUnitWidth, row.SceneUnitHeight);
    private static async Task<(Room Room, Subroom Subroom)> SeedAsync(MigratedSqliteFixture fixture) { var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 }; var subroom = new Subroom { RoomId = room.Id, FriendlyName = "one", ReferenceId = "one", Notes = "old", SortOrder = 0 }; await using var db = fixture.CreateDbContext(); db.AddRange(room, subroom); await db.SaveChangesAsync(); return (room, subroom); }
}
