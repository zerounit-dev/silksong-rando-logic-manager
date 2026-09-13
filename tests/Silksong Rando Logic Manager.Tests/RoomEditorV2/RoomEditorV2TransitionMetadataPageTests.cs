using Bunit;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Rendered V2 page coverage backed by a migration-current SQLite database.</summary>
public sealed class RoomEditorV2TransitionMetadataPageTests
{
    [Fact]
    public async Task ActiveTransitionMetadataModal_RendersNineFieldsDangerAndAutocompleteOff_ThenAppliesOneFreshView()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, transition) = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));

        page.Find($"#v2-transition-metadata-{transition.Id}").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        var dialog = page.Find("#v2-page-modal-dialog");
        foreach (var label in new[] { "game ID", "in-game X", "in-game Y", "in-game Z", "local X", "local Y", "local Z", "annotation X", "annotation Y" })
            Assert.Equal("off", dialog.QuerySelector($"[aria-label='{label}']")!.GetAttribute("autocomplete"));
        Assert.Contains("danger", dialog.QuerySelector("[aria-label='game ID']")!.ParentElement!.ClassName);
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        var blocked = await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(Baseline(transition), Draft(transition, "blocked"), "blocked"));
        Assert.Equal(V2TransitionCommandStatus.ExpectedFailure, blocked.Status);

        Set(dialog, "game ID", "updated-game"); Set(dialog, "in-game X", "1"); Set(dialog, "in-game Y", "2"); Set(dialog, "in-game Z", "3");
        Set(dialog, "local X", "4"); Set(dialog, "local Y", "5"); Set(dialog, "local Z", "6"); Set(dialog, "annotation X", "7"); Set(dialog, "annotation Y", "8");
        dialog.QuerySelectorAll("button").Single(x => x.TextContent!.Trim() == "Apply").Click();
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Equal(2, loader.LoadCount); Assert.Contains("refresh #2", page.Markup); });
        await using var verify = fixture.CreateDbContext(); var saved = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
        Assert.Equal(("updated-game", 1d, 2d, 3d, 4d, 5d, 6d, 7d, 8d), (saved.InGameId, saved.InGamePositionX, saved.InGamePositionY, saved.InGamePositionZ, saved.LocalPositionX, saved.LocalPositionY, saved.LocalPositionZ, saved.AnnotationSceneUnitX, saved.AnnotationSceneUnitY));
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == $"v2-transition-metadata-{transition.Id}");
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("backdrop")]
    public async Task MetadataModal_CancelEscapeAndBackdrop_WriteNothing(string route)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, transition) = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var _); var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.Find($"#v2-transition-metadata-{transition.Id}").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        Set(page.Find("#v2-page-modal-dialog"), "game ID", "discarded");
        if (route == "cancel") page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == "Cancel").Click();
        else if (route == "escape") page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        else page.Find(".v2-page-modal-backdrop").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        await using var verify = fixture.CreateDbContext(); Assert.Equal("duplicate-id", (await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id)).InGameId);
    }

    [Fact]
    public async Task MetadataModal_Conflict_PerformsFreshCorrectionFocusAtInitiator()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, transition) = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader); var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.Find($"#v2-transition-metadata-{transition.Id}").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        Set(page.Find("#v2-page-modal-dialog"), "game ID", "local");
        await using (var external = fixture.CreateDbContext()) { var durable = await external.RoomTransitions.SingleAsync(x => x.Id == transition.Id); durable.InGameId = "external"; await external.SaveChangesAsync(); }
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == "Apply").Click();
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Equal(2, loader.LoadCount); });
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == $"v2-transition-metadata-{transition.Id}");
    }

    [Fact]
    public async Task MetadataModal_MissingTransitionAfterRefresh_FocusesRoomFriendlyNameFallback()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, transition) = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader); var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.Find($"#v2-transition-metadata-{transition.Id}").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        await using (var external = fixture.CreateDbContext()) { external.Remove(await external.RoomTransitions.SingleAsync(x => x.Id == transition.Id)); await external.SaveChangesAsync(); }
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == "Apply").Click();
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Equal(2, loader.LoadCount); Assert.Empty(page.FindAll($"#v2-transition-metadata-{transition.Id}")); });
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "room-friendly-name");
        Assert.DoesNotContain(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == $"v2-transition-metadata-{transition.Id}");
    }

    [Fact]
    public async Task MetadataApply_CommittingBlocksDurableCommandsAndNavigationAndDisablesDismissalAndActions()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, transition) = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var _); var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.Find($"#v2-transition-metadata-{transition.Id}").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));

        await page.InvokeAsync(() => page.Instance.BeginModalTestCommitAsync());
        page.WaitForAssertion(() =>
        {
            var dialog = page.Find("#v2-page-modal-dialog"); Assert.Equal("Committing", dialog.GetAttribute("data-v2-modal-stage"));
            Assert.All(dialog.QuerySelectorAll("input,button"), control => Assert.True(control.HasAttribute("disabled")));
        });
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        Assert.Equal(V2TransitionCommandStatus.ExpectedFailure, (await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(Baseline(transition), Draft(transition, "blocked"), "blocked"))).Status);
        page.Find(".v2-page-modal-backdrop").Click(); page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Single(page.FindAll("#v2-page-modal-dialog"));

    }

    [Fact]
    public async Task RenderedMetadataApply_HeldCommittingBlocksEverythingThenCommitsRefreshesAndFocusesInitiatorOnce()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, transition) = await SeedAsync(fixture);
        var production = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var commands = new HeldTransitionMetadataCommands(production);
        using var context = PageContext(fixture, out var loader, commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        var initiatorId = $"v2-transition-metadata-{transition.Id}";

        page.Find($"#{initiatorId}").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        Set(page.Find("#v2-page-modal-dialog"), "game ID", "held-apply");

        // This is the real rendered Apply event. The test wrapper observes that
        // production Apply is now waiting before allowing its SQLite command.
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == "Apply").Click();
        await commands.WaitUntilEnteredAsync();
        page.WaitForAssertion(() =>
        {
            var dialog = page.Find("#v2-page-modal-dialog");
            Assert.Equal("Committing", dialog.GetAttribute("data-v2-modal-stage"));
            Assert.All(dialog.QuerySelectorAll("input,button"), control => Assert.True(control.HasAttribute("disabled")));
        });
        Assert.Equal(1, commands.MetadataCalls);
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        // A second rendered metadata action is a second durable-command admission
        // attempt; the open committing modal rejects it before it reaches the
        // held production metadata command.
        page.Find($"#{initiatorId}").Click();
        page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.Find(".v2-page-modal-backdrop").Click();
        Assert.Single(page.FindAll("#v2-page-modal-dialog"));
        Assert.Equal(1, commands.MetadataCalls);
        Assert.Equal(1, loader.LoadCount);

        commands.Release();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(2, loader.LoadCount);
            Assert.Contains("refresh #2", page.Markup);
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == initiatorId));
        });
        Assert.Equal(1, commands.MetadataCalls);
        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField"));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("held-apply", (await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id)).InGameId);
    }

    private static TestContext PageContext(MigratedSqliteFixture fixture, out CountingLoader loader, IRoomEditorV2CommandService? commands = null)
    {
        var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true); context.JSInterop.SetupVoid("focusEditorField", _ => true);
        loader = new(new RoomEditorV2LogicLoader(fixture));
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands ?? new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        return context;
    }
    private static async Task<(Room Room, RoomTransition Transition)> SeedAsync(MigratedSqliteFixture fixture)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", SortOrder = 0, InGameId = "duplicate-id" };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", SortOrder = 0, InGameId = "duplicate-id" };
        await using var db = fixture.CreateDbContext(); db.AddRange(room, transition, check); await db.SaveChangesAsync(); return (room, transition);
    }
    private static void Set(IElement dialog, string label, string value) => dialog.QuerySelector($"[aria-label='{label}']")!.Input(value);
    private static TransitionDurableBaseline Baseline(RoomTransition x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified);
    private static TransitionDraft Draft(RoomTransition x, string notes) => new(Guid.NewGuid(), x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, notes, x.IsTodo, x.IsVerified);
    private sealed class CountingLoader(IRoomEditorV2LogicLoader inner) : IRoomEditorV2LogicLoader { public int LoadCount { get; private set; } public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken token) { LoadCount++; return await inner.LoadAsync(roomId, token); } }
    /// <summary>Test-only decorator: only the metadata command is held; all persistence remains the production implementation.</summary>
    private sealed class HeldTransitionMetadataCommands(IRoomEditorV2CommandService inner) : IRoomEditorV2CommandService
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MetadataCalls { get; private set; }
        public async Task WaitUntilEnteredAsync() => await entered.Task;
        public void Release() => release.TrySetResult();
        public async Task<V2TransitionCommandOutcome> SaveTransitionMetadataAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionInGameMetadataDraft draft)
        {
            MetadataCalls++;
            entered.TrySetResult();
            await release.Task;
            return await inner.SaveTransitionMetadataAsync(roomId, baseline, draft);
        }
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => inner.SaveSubroomAsync(roomId, baseline, draft);
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => inner.CreateSubroomAsync(roomId, draft);
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => inner.ReorderSubroomAsync(roomId, entityId, targetIndex);
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => inner.SetSubroomArchiveAsync(roomId, entityId, archived);
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => inner.DeleteSubroomAsync(roomId, entityId);
    }
}
