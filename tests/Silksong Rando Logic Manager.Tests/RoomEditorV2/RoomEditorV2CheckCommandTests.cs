using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for the Phase 2b typed check adapter.</summary>
public sealed class RoomEditorV2CheckCommandTests
{
    [Fact]
    public async Task Save_DiffsAllEditableFields_AndNoOpDoesNotAdvanceTimestamp()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, check) = await SeedAsync(fixture); var commands = Commands(fixture); var baseline = Baseline(check);
        var committed = await commands.SaveCheckAsync(room.Id, baseline, Draft("local", "sub", "req", "notes", false, true, true));
        Assert.Equal(V2CheckCommandStatus.Committed, committed.Status);
        await using (var verify = fixture.CreateDbContext()) { var saved = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id); Assert.Equal(("local", "sub", "req", "notes", false, true, true), (saved.FriendlyName, saved.SubroomReferenceText, saved.Requirements, saved.Notes, saved.IsIncludedInApworld, saved.IsTodo, saved.IsVerified)); baseline = Baseline(saved); }
        Assert.Equal(V2CheckCommandStatus.Unchanged, (await commands.SaveCheckAsync(room.Id, baseline, Draft("local", "sub", "req", "notes", false, true, true))).Status);
        await using var unchanged = fixture.CreateDbContext(); Assert.Equal(baseline.UpdatedUtc, (await unchanged.CheckLocations.SingleAsync(x => x.Id == check.Id)).UpdatedUtc);
    }

    [Fact]
    public async Task Save_ClearsNullableReferenceAndUnknownVerification()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, check) = await SeedAsync(fixture); var commands = Commands(fixture);
        await using (var setup = fixture.CreateDbContext()) { var row = await setup.CheckLocations.SingleAsync(x => x.Id == check.Id); row.SubroomReferenceText = "sub"; row.IsVerified = true; await setup.SaveChangesAsync(); check = await setup.CheckLocations.AsNoTracking().SingleAsync(x => x.Id == check.Id); }
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.SaveCheckAsync(room.Id, Baseline(check), Draft("old", null, "old", "old", true, false, null))).Status);
        await using var verify = fixture.CreateDbContext(); var saved = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id); Assert.Null(saved.SubroomReferenceText); Assert.Null(saved.IsVerified);
    }

    [Fact]
    public async Task Save_MergesDisjointExternalField_ConflictsOnOverlap_AndReportsMissing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, check) = await SeedAsync(fixture); var commands = Commands(fixture); var baseline = Baseline(check);
        await using (var external = fixture.CreateDbContext()) { var row = await external.CheckLocations.SingleAsync(x => x.Id == check.Id); row.Notes = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.SaveCheckAsync(room.Id, baseline, Draft("local", null, "old", "old", true, false, null))).Status);
        await using (var saved = fixture.CreateDbContext()) { var row = await saved.CheckLocations.SingleAsync(x => x.Id == check.Id); Assert.Equal(("local", "external"), (row.FriendlyName, row.Notes)); baseline = Baseline(row); }
        await using (var external = fixture.CreateDbContext()) { var row = await external.CheckLocations.SingleAsync(x => x.Id == check.Id); row.FriendlyName = "external name"; await external.SaveChangesAsync(); }
        var conflict = await commands.SaveCheckAsync(room.Id, baseline, Draft("local again", null, "old", "external", true, false, null));
        Assert.Equal(V2CheckCommandStatus.Conflict, conflict.Status); Assert.Equal("external name", conflict.FreshBaseline!.FriendlyName); Assert.Equal("local again", conflict.RetainedDraft!.FriendlyName);
        await using (var delete = fixture.CreateDbContext()) { delete.Remove(await delete.CheckLocations.SingleAsync(x => x.Id == check.Id)); await delete.SaveChangesAsync(); }
        Assert.Equal(V2CheckCommandStatus.Missing, (await commands.SaveCheckAsync(room.Id, baseline, Draft("missing", null, "old", "", true, false, null))).Status);
    }

    [Fact]
    public async Task Create_ReorderLifecycleAndDeletion_RespectPartitionsAndResolverState()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, first) = await SeedAsync(fixture); var second = new CheckLocation { RoomId = room.Id, FriendlyName = "second", SubroomReferenceText = "sub", Requirements = "r", SortOrder = 1 };
        var subroom = new Subroom { RoomId = room.Id, FriendlyName = "Sub", ReferenceId = "sub", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(second, subroom); await seed.SaveChangesAsync(); await new LogicReferenceResolver(seed).ResolveAsync(); }
        var commands = Commands(fixture); var created = await commands.CreateCheckAsync(room.Id, Draft("created", "sub", "requirement", "note", false, true, false)); Assert.Equal(V2CheckCommandStatus.Committed, created.Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.ReorderCheckAsync(room.Id, second.Id, 0)).Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.SetCheckArchiveAsync(room.Id, first.Id, true)).Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.ReorderCheckAsync(room.Id, first.Id, 0)).Status);
        Assert.Equal(V2CheckCommandStatus.ExpectedFailure, (await commands.DeleteCheckAsync(room.Id, second.Id)).Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.DeleteCheckAsync(room.Id, first.Id)).Status);
        await using var verify = fixture.CreateDbContext(); Assert.Null(await verify.CheckLocations.SingleOrDefaultAsync(x => x.Id == first.Id)); Assert.Equal([second.Id, created.CreatedEntityId!.Value], await verify.CheckLocations.Where(x => x.RoomId == room.Id && !x.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToArrayAsync());
        Assert.Equal("sub", (await verify.CheckLocations.SingleAsync(x => x.Id == second.Id)).SubroomReferenceText);
    }

    private static RoomEditorV2CommandService Commands(MigratedSqliteFixture fixture) => new(new LogicCatalogService(fixture));
    private static CheckDraft Draft(string name, string? subroom, string requirements, string notes, bool apworld, bool todo, bool? verification) => new(Guid.NewGuid(), name, subroom, requirements, notes, apworld, todo, verification);
    private static CheckDurableBaseline Baseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.IsIncludedInApworld, x.IsTodo, x.IsVerified);
    private static async Task<(Room Room, CheckLocation Check)> SeedAsync(MigratedSqliteFixture fixture) { var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 }; var check = new CheckLocation { RoomId = room.Id, FriendlyName = "old", Requirements = "old", Notes = "old", SortOrder = 0, IsIncludedInApworld = true }; await using var db = fixture.CreateDbContext(); db.AddRange(room, check); await db.SaveChangesAsync(); return (room, check); }
}
