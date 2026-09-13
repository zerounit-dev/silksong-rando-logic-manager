using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2RoomLifecyclePageTests
{
    [Fact]
    public async Task ArchiveRestoreAndArchivedDelete_UseOneRefreshModalFocusAndLandingDestination()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true); context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var roomId = Guid.NewGuid(); var loader = new LifecycleLoader(View(roomId, false)); var commands = new LifecycleCommands(loader);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        page.Find("#room-archive").Click();
        page.WaitForAssertion(() => { Assert.Equal(1, commands.ArchiveCalls); Assert.Equal(2, loader.LoadCount); Assert.NotNull(page.Find("#room-restore")); Assert.Empty(page.FindAll(".room-map-context")); });
        page.Find("#room-restore").Click();
        page.WaitForAssertion(() => { Assert.Equal(1, commands.RestoreCalls); Assert.Equal(3, loader.LoadCount); Assert.NotNull(page.Find("#room-archive")); });

        // Delete admission is archived-only and cancellation returns to its trigger without a write.
        page.Find("#room-archive").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#room-permanent-delete")));
        page.Find("#room-permanent-delete").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        page.Find("#v2-page-modal-dialog").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(0, commands.DeleteCalls);
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "room-permanent-delete");

        page.Find("#room-permanent-delete").Click(); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click();
        page.WaitForAssertion(() => { Assert.Equal(1, commands.DeleteCalls); Assert.Empty(page.FindAll(".room-document")); Assert.EndsWith("/", context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri); });
    }

    [Fact]
    public void LifecycleControls_RenderActiveArchiveOrArchivedRestoreDeleteOnly()
    {
        using var context = new TestContext();
        var active = context.RenderComponent<RoomLifecycleContentControlsPresentation>(p => p.Add(x => x.View, new RoomLifecycleContentControlsView(false, false)));
        Assert.NotNull(active.Find("#room-archive")); Assert.Empty(active.FindAll("#room-restore, #room-permanent-delete, #room-scene-dimensions"));
        var archived = context.RenderComponent<RoomLifecycleContentControlsPresentation>(p => p.Add(x => x.View, new RoomLifecycleContentControlsView(true, false)));
        Assert.Empty(archived.FindAll("#room-archive")); Assert.NotNull(archived.Find("#room-restore")); Assert.NotNull(archived.Find("#room-permanent-delete"));
    }

    [Fact]
    public void ArchivedPermanentDeleteFailure_ClosesDialogRetainsRoomAndReturnsCorrectionFocus()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true); context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var roomId = Guid.NewGuid(); var loader = new LifecycleLoader(View(roomId, true)); var commands = new LifecycleCommands(loader) { DeleteOutcome = new(V2RoomLifecycleCommandStatus.ExpectedFailure, "Delete failed.") };
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        page.Find("#room-permanent-delete").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, commands.DeleteCalls); Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.NotNull(page.Find(".room-document")); Assert.NotNull(page.Find("#room-permanent-delete"));
            Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "room-permanent-delete");
        });
    }

    private static RoomEditorV2View View(Guid id, bool archived) => new(new(id, "Room", "room", null, null, null, null, null, false, false, archived, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral), new([], []), new([], [], [], new Dictionary<Guid, IReadOnlyList<string>>()), new([], [], []), new([], [], []));
    private sealed class LifecycleLoader(RoomEditorV2View view) : IRoomEditorV2LogicLoader
    {
        public RoomEditorV2View? Current { get; set; } = view; public int LoadCount { get; private set; }
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { LoadCount++; return Task.FromResult(Current); }
    }
    private sealed class LifecycleCommands(LifecycleLoader loader) : IRoomEditorV2CommandService
    {
        public int ArchiveCalls { get; private set; } public int RestoreCalls { get; private set; } public int DeleteCalls { get; private set; }
        public V2RoomLifecycleCommandOutcome DeleteOutcome { get; init; } = new(V2RoomLifecycleCommandStatus.Committed);
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
        public Task<V2RoomLifecycleCommandOutcome> SetRoomArchiveAsync(Guid roomId, bool archived)
        {
            if (archived) ArchiveCalls++; else RestoreCalls++;
            loader.Current = loader.Current! with { Header = loader.Current.Header with { IsArchived = archived } };
            return Task.FromResult(new V2RoomLifecycleCommandOutcome(V2RoomLifecycleCommandStatus.Committed));
        }
        public Task<V2RoomLifecycleCommandOutcome> DeleteRoomAsync(Guid roomId) { DeleteCalls++; if (DeleteOutcome.Status == V2RoomLifecycleCommandStatus.Committed) loader.Current = null; return Task.FromResult(DeleteOutcome); }
    }
}
