using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for the typed V2 dimensions command.</summary>
public sealed class RoomEditorV2SceneDimensionsCommandTests
{
    [Fact]
    public async Task CompleteClearInvalidNoOpConflictAndMissing_PreserveSettledImageRules()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 20,
            SceneImageScaleXPercent = 100, SceneImageScaleYPercent = 100, SceneImagePanXPercent = 0, SceneImagePanYPercent = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.Add(room); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var baseline = new RoomSceneDimensionsDurableBaseline(room.Id, room.UpdatedUtc, 10, 20);

        Assert.Equal(V2RoomSceneDimensionsCommandStatus.Committed, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(30, 40))).Status);
        await using (var verify = fixture.CreateDbContext()) { var saved = await verify.Rooms.SingleAsync(x => x.Id == room.Id); Assert.Equal((30d, 40d, true), (saved.SceneUnitWidth, saved.SceneUnitHeight, saved.IsSceneImageStale)); room = saved; }
        baseline = new(room.Id, room.UpdatedUtc, room.SceneUnitWidth, room.SceneUnitHeight);
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.Unchanged, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(30, 40))).Status);
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.ExpectedFailure, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(30, null))).Status);
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.ExpectedFailure, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(double.NaN, 40))).Status);
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.ExpectedFailure, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(0, 40))).Status);
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.Committed, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(null, null))).Status);
        await using (var clear = fixture.CreateDbContext()) { var saved = await clear.Rooms.SingleAsync(x => x.Id == room.Id); Assert.Null(saved.SceneUnitWidth); Assert.Null(saved.SceneUnitHeight); Assert.True(saved.IsSceneImageStale); Assert.Equal(100, saved.SceneImageScaleXPercent); room = saved; }
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.Conflict, (await commands.SaveRoomSceneDimensionsAsync(room.Id, baseline, new(1, 1))).Status);
        Assert.Equal(V2RoomSceneDimensionsCommandStatus.Missing, (await commands.SaveRoomSceneDimensionsAsync(Guid.NewGuid(), baseline, new(1, 1))).Status);
    }

    [Fact]
    public async Task WrongRoomBaselineAndArchivedTransition_AreMissingWithoutWrites()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var target = new Room { Id = Guid.NewGuid(), FriendlyName = "target", ReferenceId = "target" };
        var other = new Room { Id = Guid.NewGuid(), FriendlyName = "other", ReferenceId = "other" };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(target, other); await seed.SaveChangesAsync(); }
        var archived = new RoomTransition { RoomId = target.Id, Alias = "a", FriendlyName = "archived", Requirements = "r", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        await using (var seed = fixture.CreateDbContext()) { seed.Add(archived); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);

        var dimensions = await commands.SaveRoomSceneDimensionsAsync(target.Id,
            new(other.Id, other.UpdatedUtc, other.SceneUnitWidth, other.SceneUnitHeight), new(12, 24));
        var transition = await commands.SaveTransitionAsync(target.Id, TransitionBaseline(archived), TransitionDraft(archived, "changed"));

        Assert.Equal(V2RoomSceneDimensionsCommandStatus.Missing, dimensions.Status);
        Assert.Equal(V2TransitionCommandStatus.Missing, transition.Status);
        await using var verify = fixture.CreateDbContext();
        var durableTarget = await verify.Rooms.SingleAsync(x => x.Id == target.Id);
        var durableTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == archived.Id);
        Assert.Null(durableTarget.SceneUnitWidth); Assert.Null(durableTarget.SceneUnitHeight);
        Assert.Equal("archived", durableTransition.FriendlyName); Assert.True(durableTransition.IsArchived);
    }

    private static TransitionDurableBaseline TransitionBaseline(RoomTransition row) => new(row.Id, row.UpdatedUtc, row.SortOrder, row.IsArchived, row.Alias, row.FriendlyName, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.SourceSubroomReferenceText, row.DestinationRoomReferenceText, row.DestinationTransitionAliasText, row.Requirements, row.Notes, row.IsTodo, row.IsVerified, row.EnableAnnotation);
    private static TransitionDraft TransitionDraft(RoomTransition row, string name) => new(Guid.NewGuid(), row.Alias, name, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.SourceSubroomReferenceText, row.DestinationRoomReferenceText, row.DestinationTransitionAliasText, row.Requirements, row.Notes, row.IsTodo, row.IsVerified);
}
