using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite proof for the 4t.2 immediate annotation command layer.</summary>
public sealed class RoomEditorV2AnnotationCommandTests
{
    [Fact]
    public async Task ResetClearShowHideAndShowAndSelect_PreserveOnlyTheSettledFieldsAndReturnSceneImpact()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r", InGamePositionX = 4, InGamePositionY = 5, AnnotationSceneUnitX = 20, AnnotationSceneUnitY = 21, EnableAnnotation = false };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", InGamePositionX = 6, InGamePositionY = 7, AnnotationSceneUnitX = 30, AnnotationSceneUnitY = 31, EnableAnnotation = false };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "a", ReferenceId = "a" }; var b = new Subroom { RoomId = room.Id, FriendlyName = "b", ReferenceId = "b" };
        var forward = new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", SceneUnitX = 8, SceneUnitY = 9, EnableAnnotation = false };
        var reverse = new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "path", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", Requirements = "r", SceneUnitX = 8, SceneUnitY = 9, EnableAnnotation = false, SortOrder = 1 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition, check, a, b, forward, reverse); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);

        var transitionBaseline = TransitionBaseline(transition); var checkBaseline = CheckBaseline(check);
        var transitionShow = await commands.ShowAndSelectTransitionAnnotationAsync(room.Id, transitionBaseline);
        var checkShow = await commands.ShowAndSelectCheckAnnotationAsync(room.Id, checkBaseline);
        var connectionShow = await commands.ShowAndSelectConnectionAnnotationAsync(room.Id, forward.Id);
        Assert.All(new[] { transitionShow.AnnotationRefreshImpact, checkShow.AnnotationRefreshImpact, connectionShow.AnnotationRefreshImpact }, x => Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, x));
        Assert.Equal((V2AnnotationSelectionKind.Transition, transition.Id), (transitionShow.PostRefreshSelection!.Kind, transitionShow.PostRefreshSelection.EntityId));
        Assert.Equal((V2AnnotationSelectionKind.Check, check.Id), (checkShow.PostRefreshSelection!.Kind, checkShow.PostRefreshSelection.EntityId));
        Assert.Equal((V2AnnotationSelectionKind.Connection, forward.Id), (connectionShow.PostRefreshSelection!.Kind, connectionShow.PostRefreshSelection.EntityId));

        await using (var reload = fixture.CreateDbContext()) { transition = await reload.RoomTransitions.SingleAsync(x => x.Id == transition.Id); check = await reload.CheckLocations.SingleAsync(x => x.Id == check.Id); }
        var transitionReset = await commands.ResetTransitionAnnotationAsync(room.Id, TransitionBaseline(transition));
        var checkReset = await commands.ResetCheckAnnotationAsync(room.Id, CheckBaseline(check));
        Assert.All(new[] { transitionReset.AnnotationRefreshImpact, checkReset.AnnotationRefreshImpact }, x => Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, x));
        Assert.Null(transitionReset.PostRefreshSelection); Assert.Null(checkReset.PostRefreshSelection);
        await using (var reload = fixture.CreateDbContext()) { transition = await reload.RoomTransitions.SingleAsync(x => x.Id == transition.Id); check = await reload.CheckLocations.SingleAsync(x => x.Id == check.Id); }
        var transitionHide = await commands.RemoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition));
        var checkHide = await commands.RemoveCheckAnnotationAsync(room.Id, CheckBaseline(check));
        Assert.All(new[] { transitionHide.AnnotationRefreshImpact, checkHide.AnnotationRefreshImpact }, x => Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, x));
        Assert.Null(transitionHide.PostRefreshSelection); Assert.Null(checkHide.PostRefreshSelection);
        await using (var reload = fixture.CreateDbContext()) { transition = await reload.RoomTransitions.SingleAsync(x => x.Id == transition.Id); check = await reload.CheckLocations.SingleAsync(x => x.Id == check.Id); }
        var transitionShowOnly = await commands.ShowTransitionAnnotationAsync(room.Id, TransitionBaseline(transition));
        var checkShowOnly = await commands.ShowCheckAnnotationAsync(room.Id, CheckBaseline(check));
        Assert.All(new[] { transitionShowOnly.AnnotationRefreshImpact, checkShowOnly.AnnotationRefreshImpact }, x => Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, x));
        Assert.Null(transitionShowOnly.PostRefreshSelection); Assert.Null(checkShowOnly.PostRefreshSelection);
        await using (var reload = fixture.CreateDbContext()) { transition = await reload.RoomTransitions.SingleAsync(x => x.Id == transition.Id); check = await reload.CheckLocations.SingleAsync(x => x.Id == check.Id); }
        var transitionClear = await commands.ClearTransitionAnnotationAsync(room.Id, TransitionBaseline(transition));
        var checkClear = await commands.ClearCheckAnnotationAsync(room.Id, CheckBaseline(check));
        Assert.All(new[] { transitionClear.AnnotationRefreshImpact, checkClear.AnnotationRefreshImpact }, x => Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, x));
        Assert.Null(transitionClear.PostRefreshSelection); Assert.Null(checkClear.PostRefreshSelection);
        var subroomBaseline = new SubroomDurableBaseline(a.Id, a.UpdatedUtc, a.SortOrder, a.IsArchived, a.FriendlyName, a.ReferenceId, a.Notes ?? "", 1, 2, 3, 4);
        await using (var geometry = fixture.CreateDbContext()) { var row = await geometry.Subrooms.SingleAsync(x => x.Id == a.Id); row.SceneUnitX = 1; row.SceneUnitY = 2; row.SceneUnitWidth = 3; row.SceneUnitHeight = 4; await geometry.SaveChangesAsync(); subroomBaseline = subroomBaseline with { UpdatedUtc = row.UpdatedUtc }; }
        Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, (await commands.ClearSubroomAnnotationAsync(room.Id, subroomBaseline)).AnnotationRefreshImpact);
        var connectionHideBeforeClear = await commands.DisableConnectionAnnotationAsync(room.Id, reverse.Id);
        var connectionShowOnly = await commands.ShowConnectionAnnotationAsync(room.Id, forward.Id);
        var connectionClear = await commands.ClearConnectionAnnotationAsync(room.Id, forward.Id);
        var connectionHide = await commands.DisableConnectionAnnotationAsync(room.Id, reverse.Id);
        Assert.Equal(V2ConnectionCommandStatus.Committed, connectionHideBeforeClear.Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, connectionShowOnly.Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, connectionClear.Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, connectionHide.Status);
        Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, connectionHideBeforeClear.AnnotationRefreshImpact);
        Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, connectionShowOnly.AnnotationRefreshImpact);
        Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, connectionClear.AnnotationRefreshImpact);
        Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, connectionHide.AnnotationRefreshImpact);
        Assert.Null(connectionHideBeforeClear.PostRefreshSelection); Assert.Null(connectionShowOnly.PostRefreshSelection);
        Assert.Null(connectionClear.PostRefreshSelection); Assert.Null(connectionHide.PostRefreshSelection);

        await using var verify = fixture.CreateDbContext();
        var savedTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id); var savedCheck = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
        Assert.True(savedTransition.EnableAnnotation); Assert.Null(savedTransition.AnnotationSceneUnitX); Assert.Null(savedTransition.AnnotationSceneUnitY); Assert.Equal((4d, 5d), (savedTransition.InGamePositionX, savedTransition.InGamePositionY));
        Assert.True(savedCheck.EnableAnnotation); Assert.Null(savedCheck.AnnotationSceneUnitX); Assert.Null(savedCheck.AnnotationSceneUnitY); Assert.Equal((6d, 7d), (savedCheck.InGamePositionX, savedCheck.InGamePositionY));
        var savedSubroom = await verify.Subrooms.SingleAsync(x => x.Id == a.Id); Assert.Null(savedSubroom.SceneUnitX); Assert.Null(savedSubroom.SceneUnitY); Assert.Null(savedSubroom.SceneUnitWidth); Assert.Null(savedSubroom.SceneUnitHeight);
        Assert.All(await verify.SubroomConnections.Where(x => x.Alias == "p").ToListAsync(), x => { Assert.False(x.EnableAnnotation); Assert.Null(x.SceneUnitX); Assert.Null(x.SceneUnitY); });
    }

    [Fact]
    public async Task Commands_ReturnMissingConflictAndNoopWithoutWriting()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" }; var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r", InGamePositionX = 1, InGamePositionY = 2, AnnotationSceneUnitX = 1, AnnotationSceneUnitY = 2, EnableAnnotation = true };
        var unplacedCheck = new CheckLocation { RoomId = room.Id, FriendlyName = "unplaced", Requirements = "r", EnableAnnotation = false };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, other, transition, unplacedCheck); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture); var baseline = TransitionBaseline(transition);
        var unchanged = await commands.ShowTransitionAnnotationAsync(room.Id, baseline);
        var missing = await commands.ResetTransitionAnnotationAsync(other.Id, baseline);
        Assert.Equal(V2TransitionCommandStatus.Unchanged, unchanged.Status);
        Assert.Equal(V2TransitionCommandStatus.Missing, missing.Status);
        Assert.Equal(V2AnnotationRefreshImpact.None, unchanged.AnnotationRefreshImpact);
        Assert.Equal(V2AnnotationRefreshImpact.None, missing.AnnotationRefreshImpact);
        Assert.Null(unchanged.PostRefreshSelection); Assert.Null(missing.PostRefreshSelection);
        var missingShowAndSelect = await commands.ShowAndSelectTransitionAnnotationAsync(other.Id, baseline);
        var ineligibleShowAndSelect = await commands.ShowAndSelectCheckAnnotationAsync(room.Id, CheckBaseline(unplacedCheck));
        Assert.Equal(V2TransitionCommandStatus.Missing, missingShowAndSelect.Status);
        Assert.Equal(V2CheckCommandStatus.ExpectedFailure, ineligibleShowAndSelect.Status);
        Assert.Equal(V2AnnotationRefreshImpact.None, missingShowAndSelect.AnnotationRefreshImpact);
        Assert.Equal(V2AnnotationRefreshImpact.None, ineligibleShowAndSelect.AnnotationRefreshImpact);
        Assert.Null(missingShowAndSelect.PostRefreshSelection); Assert.Null(ineligibleShowAndSelect.PostRefreshSelection);
        await using (var concurrent = fixture.CreateDbContext()) { var row = await concurrent.RoomTransitions.SingleAsync(x => x.Id == transition.Id); row.AnnotationSceneUnitX = 10; row.UpdatedUtc = row.UpdatedUtc.AddTicks(1); await concurrent.SaveChangesAsync(); }
        var conflict = await commands.ClearTransitionAnnotationAsync(room.Id, baseline);
        Assert.Equal(V2TransitionCommandStatus.Conflict, conflict.Status);
        Assert.Equal(V2AnnotationRefreshImpact.None, conflict.AnnotationRefreshImpact);
        Assert.Null(conflict.PostRefreshSelection);
    }

    [Fact]
    public async Task SubroomShowAndHide_RejectStaleBaselineBeforeMutationWithFreshBaseline()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var subroom = new Subroom { RoomId = room.Id, FriendlyName = "subroom", ReferenceId = "subroom", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4, EnableAnnotation = false };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, subroom); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var stale = new SubroomDurableBaseline(subroom.Id, subroom.UpdatedUtc, subroom.SortOrder, subroom.IsArchived, subroom.FriendlyName, subroom.ReferenceId, subroom.Notes ?? "", subroom.SceneUnitX, subroom.SceneUnitY, subroom.SceneUnitWidth, subroom.SceneUnitHeight, subroom.EnableAnnotation);
        await using (var external = fixture.CreateDbContext()) { var row = await external.Subrooms.SingleAsync(x => x.Id == subroom.Id); row.Notes = "external"; row.UpdatedUtc = row.UpdatedUtc.AddTicks(1); await external.SaveChangesAsync(); }

        var show = await commands.ShowSubroomAnnotationAsync(room.Id, stale);
        var hide = await commands.HideSubroomAnnotationAsync(room.Id, stale);

        Assert.Equal(V2SubroomCommandStatus.Conflict, show.Status); Assert.Equal(V2SubroomCommandStatus.Conflict, hide.Status);
        Assert.NotNull(show.FreshBaseline); Assert.NotNull(hide.FreshBaseline);
        await using var verify = fixture.CreateDbContext(); var saved = await verify.Subrooms.SingleAsync(x => x.Id == subroom.Id);
        Assert.False(saved.EnableAnnotation); Assert.Equal("external", saved.Notes); Assert.Equal(saved.UpdatedUtc, show.FreshBaseline!.UpdatedUtc); Assert.Equal(saved.UpdatedUtc, hide.FreshBaseline!.UpdatedUtc);
    }

    [Fact]
    public async Task SubroomShowHideAndClear_UseCurrentRoomActiveOwnershipAndPreserveGeometry()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" }; var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var subroom = new Subroom { RoomId = room.Id, FriendlyName = "subroom", ReferenceId = "subroom", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4, EnableAnnotation = false };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, other, subroom); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var show = await commands.ShowSubroomAnnotationAsync(room.Id, SubroomBaseline(subroom));
        Assert.Equal(V2SubroomCommandStatus.Committed, show.Status); Assert.Equal(V2AnnotationRefreshImpact.CanvasAndViewport, show.AnnotationRefreshImpact);
        await using (var afterShow = fixture.CreateDbContext()) { subroom = await afterShow.Subrooms.SingleAsync(x => x.Id == subroom.Id); Assert.True(subroom.EnableAnnotation); Assert.Equal((1d, 2d, 3d, 4d), (subroom.SceneUnitX, subroom.SceneUnitY, subroom.SceneUnitWidth, subroom.SceneUnitHeight)); }
        Assert.Equal(V2SubroomCommandStatus.Unchanged, (await commands.ShowSubroomAnnotationAsync(room.Id, SubroomBaseline(subroom))).Status);
        var hide = await commands.HideSubroomAnnotationAsync(room.Id, SubroomBaseline(subroom));
        Assert.Equal(V2SubroomCommandStatus.Committed, hide.Status);
        await using (var afterHide = fixture.CreateDbContext()) { subroom = await afterHide.Subrooms.SingleAsync(x => x.Id == subroom.Id); Assert.False(subroom.EnableAnnotation); Assert.Equal((1d, 2d, 3d, 4d), (subroom.SceneUnitX, subroom.SceneUnitY, subroom.SceneUnitWidth, subroom.SceneUnitHeight)); }
        var clear = await commands.ClearSubroomAnnotationAsync(room.Id, SubroomBaseline(subroom));
        Assert.Equal(V2SubroomCommandStatus.Committed, clear.Status);
        await using (var afterClear = fixture.CreateDbContext()) { subroom = await afterClear.Subrooms.SingleAsync(x => x.Id == subroom.Id); Assert.False(subroom.EnableAnnotation); Assert.Null(subroom.SceneUnitX); Assert.Null(subroom.SceneUnitY); Assert.Null(subroom.SceneUnitWidth); Assert.Null(subroom.SceneUnitHeight); }
        Assert.Equal(V2SubroomCommandStatus.Unchanged, (await commands.ClearSubroomAnnotationAsync(room.Id, SubroomBaseline(subroom))).Status);
        Assert.Equal(V2SubroomCommandStatus.Missing, (await commands.HideSubroomAnnotationAsync(other.Id, SubroomBaseline(subroom))).Status);
        await using (var verify = fixture.CreateDbContext()) { var saved = await verify.Subrooms.SingleAsync(x => x.Id == subroom.Id); Assert.False(saved.EnableAnnotation); Assert.Null(saved.SceneUnitX); Assert.Null(saved.SceneUnitY); Assert.Null(saved.SceneUnitWidth); Assert.Null(saved.SceneUnitHeight); }
    }

    private static TransitionDurableBaseline TransitionBaseline(RoomTransition x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified, x.EnableAnnotation);
    private static CheckMetadataDurableBaseline CheckBaseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.IsIncludedInApworld, x.EnableAnnotation, x.IsTodo, x.IsVerified, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static SubroomDurableBaseline SubroomBaseline(Subroom x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.ReferenceId, x.Notes ?? "", x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation);
}
