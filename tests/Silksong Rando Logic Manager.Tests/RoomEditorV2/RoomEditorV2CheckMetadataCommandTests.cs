using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite field-diff coverage for check metadata only.</summary>
public sealed class RoomEditorV2CheckMetadataCommandTests
{
    [Fact]
    public async Task MetadataDiff_ClearsNullablesPreservesAuthoredAndResolverFields_AndMergesOrConflicts()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "authored", SubroomReferenceText = "sub", Requirements = "requirements", Notes = "notes", SortOrder = 0, IsIncludedInApworld = false, IsTodo = true, IsVerified = true, EnableAnnotation = true, InGameId = "old", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6, AnnotationSceneUnitX = 7, AnnotationSceneUnitY = 8 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(room, check); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture)); var baseline = Baseline(check);
        var clear = new CheckInGameMetadataDraft(null, null, null, null, null, null, null, null, null);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.SaveCheckMetadataAsync(room.Id, baseline, clear)).Status);
        await using (var verify = fixture.CreateDbContext())
        {
            var saved = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
            Assert.Equal((string?)null, saved.InGameId); Assert.Null(saved.InGamePositionX); Assert.Null(saved.InGamePositionY); Assert.Null(saved.InGamePositionZ); Assert.Null(saved.LocalPositionX); Assert.Null(saved.LocalPositionY); Assert.Null(saved.LocalPositionZ); Assert.Null(saved.AnnotationSceneUnitX); Assert.Null(saved.AnnotationSceneUnitY);
            Assert.Equal(("authored", "sub", "requirements", "notes", false, true, true, true, check.ResolvedSubroomId), (saved.FriendlyName, saved.SubroomReferenceText, saved.Requirements, saved.Notes, saved.IsIncludedInApworld, saved.IsTodo, saved.IsVerified, saved.EnableAnnotation, saved.ResolvedSubroomId)); baseline = Baseline(saved);
        }
        Assert.Equal(V2CheckCommandStatus.Unchanged, (await commands.SaveCheckMetadataAsync(room.Id, baseline, clear)).Status);
        await using (var external = fixture.CreateDbContext()) { var row = await external.CheckLocations.SingleAsync(x => x.Id == check.Id); row.Notes = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.SaveCheckMetadataAsync(room.Id, baseline, clear with { AnnotationSceneUnitY = 9 })).Status);
        var stale = await Read(fixture, check.Id);
        await using (var external = fixture.CreateDbContext()) { var row = await external.CheckLocations.SingleAsync(x => x.Id == check.Id); row.InGameId = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2CheckCommandStatus.Conflict, (await commands.SaveCheckMetadataAsync(room.Id, Baseline(stale), clear with { InGameId = "local" })).Status);
        await using (var remove = fixture.CreateDbContext()) { remove.Remove(await remove.CheckLocations.SingleAsync(x => x.Id == check.Id)); await remove.SaveChangesAsync(); }
        Assert.Equal(V2CheckCommandStatus.Missing, (await commands.SaveCheckMetadataAsync(room.Id, Baseline(stale), clear)).Status);
    }
    [Fact]
    public async Task MetadataDiff_ClearAnnotationOverrideRetainsGamePositionAndOtherMetadata()
    {
        await using var fixture=await MigratedSqliteFixture.CreateAsync();var room=new Room{FriendlyName="room",ReferenceId="room",SortOrder=0};var check=new CheckLocation{RoomId=room.Id,FriendlyName="check",Requirements="r",SortOrder=0,InGameId="game",InGamePositionX=1,InGamePositionY=2,InGamePositionZ=3,LocalPositionX=4,LocalPositionY=5,LocalPositionZ=6,AnnotationSceneUnitX=7,AnnotationSceneUnitY=8};
        await using(var seed=fixture.CreateDbContext()){seed.AddRange(room,check);await seed.SaveChangesAsync();}
        var commands=new RoomEditorV2CommandService(new LogicCatalogService(fixture));Assert.Equal(V2CheckCommandStatus.Committed,(await commands.SaveCheckMetadataAsync(room.Id,Baseline(check),new(check.InGameId,check.InGamePositionX,check.InGamePositionY,check.InGamePositionZ,check.LocalPositionX,check.LocalPositionY,check.LocalPositionZ,null,null))).Status);
        await using var verify=fixture.CreateDbContext();var saved=await verify.CheckLocations.SingleAsync(x=>x.Id==check.Id);Assert.Equal(("game",1d,2d,3d,4d,5d,6d),(saved.InGameId,saved.InGamePositionX,saved.InGamePositionY,saved.InGamePositionZ,saved.LocalPositionX,saved.LocalPositionY,saved.LocalPositionZ));Assert.Null(saved.AnnotationSceneUnitX);Assert.Null(saved.AnnotationSceneUnitY);
    }
    private static CheckMetadataDurableBaseline Baseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.IsIncludedInApworld, x.EnableAnnotation, x.IsTodo, x.IsVerified, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static async Task<CheckLocation> Read(MigratedSqliteFixture fixture, Guid id) { await using var db = fixture.CreateDbContext(); return await db.CheckLocations.AsNoTracking().SingleAsync(x => x.Id == id); }
}
