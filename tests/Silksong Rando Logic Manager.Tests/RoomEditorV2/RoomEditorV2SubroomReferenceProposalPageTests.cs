using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;
using Microsoft.JSInterop;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Rendered V2 subroom-reference proposal routes, including fresh conflict correction state.</summary>
public sealed class RoomEditorV2SubroomReferenceProposalPageTests
{
    [Fact]
    public async Task RenderedProposal_UpdateAndDoNotUpdate_CommitOnceApplyCompleteViewThenFocusOnceAndBlockDurableWorkAndNavigation()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, source, target) = await SeedAsync(fixture);
        using var context = NewContext(fixture, out var loader, out var focusInterop);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        var pendingTargetId = NotesInput(page).GetAttribute("id")!;
        await OpenProposalAsync(page, "renamed", usePendingTarget: true);
        Assert.Equal(1, loader.LoadCount);

        // The open rendered proposal blocks both another durable blur and page-owned
        // application navigation; neither bypasses the pre-commit workflow.
        var table = page.FindComponent<SubroomTablePresentation>().Instance;
        await page.InvokeAsync(() => table.BlurSubroomAsync(ClientRowId(page), "name", null));
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        Assert.Equal(1, loader.LoadCount);

        var updateEvents = new ProposalRefreshEventLog(loader, focusInterop);
        updateEvents.Attach(page, pendingTargetId, "renamed", "renamed");
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "update references").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal("renamed", ReferenceInput(page).GetAttribute("value"));
            Assert.Equal(1, focusInterop.FocusCalls(pendingTargetId));
            updateEvents.AssertExactOrder(pendingTargetId);
        });
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.Equal("renamed", (await verify.Subrooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
            Assert.Equal("renamed", (await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).SourceSubroomReferenceText);
        }

        await using var secondFixture = await MigratedSqliteFixture.CreateAsync();
        var (secondRoom, secondSource, secondTarget) = await SeedAsync(secondFixture);
        using var secondContext = NewContext(secondFixture, out var secondLoader, out var secondFocusInterop);
        var secondPage = secondContext.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, secondRoom.Id));
        await OpenProposalAsync(secondPage, "renamed");
        var initiatorId = ReferenceInput(secondPage).GetAttribute("id")!;
        var doNotUpdateEvents = new ProposalRefreshEventLog(secondLoader, secondFocusInterop);
        doNotUpdateEvents.Attach(secondPage, initiatorId, "renamed", "one");
        secondPage.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "do not update references").Click();
        secondPage.WaitForAssertion(() =>
        {
            Assert.Equal(2, secondLoader.LoadCount);
            Assert.Equal(1, secondFocusInterop.FocusCalls(initiatorId));
            doNotUpdateEvents.AssertExactOrder(initiatorId);
        });
        await using var secondVerify = secondFixture.CreateDbContext();
        Assert.Equal("renamed", (await secondVerify.Subrooms.SingleAsync(x => x.Id == secondSource.Id)).ReferenceId);
        Assert.Equal("one", (await secondVerify.RoomTransitions.SingleAsync(x => x.Id == secondTarget.Id)).SourceSubroomReferenceText);
    }

    [Fact]
    public async Task RenderedProposal_RevertWritesNothingAndRestoresBaselineAndInitiatorFocus()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, source, target) = await SeedAsync(fixture);
        using var context = NewContext(fixture, out var loader, out var focusInterop);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        await OpenProposalAsync(page, "renamed");
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "revert change").Click();
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Equal(1, loader.LoadCount); Assert.Equal("one", ReferenceInput(page).GetAttribute("value")); Assert.Equal(1, focusInterop.FocusCalls(ClientRowId(page), "reference")); });
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("one", (await verify.Subrooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
        Assert.Equal("one", (await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).SourceSubroomReferenceText);
    }

    [Fact]
    public async Task RenderedProposal_CapturedTargetConflictRefreshesThenRetainsSubmittedDraftAgainstFreshBaselineAndCorrectionFocus()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, source, target) = await SeedAsync(fixture);
        using var context = NewContext(fixture, out var loader, out var focusInterop);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        await OpenProposalAsync(page, "renamed");
        await using (var external = fixture.CreateDbContext()) { var changed = await external.RoomTransitions.SingleAsync(x => x.Id == target.Id); changed.SourceSubroomReferenceText = "external"; await external.SaveChangesAsync(); }

        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "update references").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(2, loader.LoadCount); // initial load plus the required complete conflict refresh
            Assert.Equal("renamed", ReferenceInput(page).GetAttribute("value"));
            Assert.Equal(1, focusInterop.FocusCalls(ClientRowId(page), "reference"));
        });

        // A subsequent meaningful edit proves the table's baseline is the fresh
        // durable row (reference "one"), while the submitted reference survives as
        // its correction draft rather than being overwritten by reconciliation.
        var table = page.FindComponent<SubroomTablePresentation>().Instance;
        await page.InvokeAsync(() => table.BlurSubroomAsync(ClientRowId(page), "reference", null));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#v2-page-modal-dialog")));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("one", (await verify.Subrooms.SingleAsync(x => x.Id == source.Id)).ReferenceId);
        Assert.Equal("external", (await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).SourceSubroomReferenceText);
    }

    private static TestContext NewContext(MigratedSqliteFixture fixture, out CountingLoader loader, out RecordingFocusJsRuntime focusInterop)
    {
        var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        focusInterop = new();
        context.Services.AddSingleton<IJSRuntime>(focusInterop);
        loader = new(new RoomEditorV2LogicLoader(fixture));
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        return context;
    }

    private static async Task OpenProposalAsync(IRenderedComponent<RoomEditorV2Page> page, string value, bool usePendingTarget = false)
    {
        page.WaitForAssertion(() => Assert.NotNull(ReferenceInput(page)));
        var input = ReferenceInput(page); input.Input(value);
        var table = page.FindComponent<SubroomTablePresentation>().Instance;
        if (usePendingTarget)
            await page.InvokeAsync(() => table.NavigateSubroomAsync(ClientRowId(page), "reference", "right"));
        else
            await page.InvokeAsync(() => table.BlurSubroomAsync(ClientRowId(page), "reference", null));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#v2-page-modal-dialog")));
    }

    private static AngleSharp.Dom.IElement ReferenceInput(IRenderedComponent<RoomEditorV2Page> page) => page.Find("table[data-v2-subroom-table='active'] tr:not([data-v2-subroom-tail]) input[aria-label='Subroom reference ID']");
    private static AngleSharp.Dom.IElement NotesInput(IRenderedComponent<RoomEditorV2Page> page) => page.Find("table[data-v2-subroom-table='active'] tr:not([data-v2-subroom-tail]) textarea[aria-label='Subroom notes']");
    private static string ClientRowId(IRenderedComponent<RoomEditorV2Page> page) => ReferenceInput(page).GetAttribute("data-v2-subroom-client-row")!;

    private static async Task<(Room Room, Subroom Source, RoomTransition Target)> SeedAsync(MigratedSqliteFixture fixture)
    {
        var room = new Room { FriendlyName = "Room", ReferenceId = "room" };
        var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "one" };
        var target = new RoomTransition { RoomId = room.Id, FriendlyName = "Exit", Alias = "e", SourceSubroomReferenceText = "one", ResolvedSourceSubroomId = source.Id };
        await using var db = fixture.CreateDbContext(); db.AddRange(room, source, target); await db.SaveChangesAsync();
        return (room, source, target);
    }

    private sealed class CountingLoader(IRoomEditorV2LogicLoader inner) : IRoomEditorV2LogicLoader
    {
        public int LoadCount { get; private set; }
        public List<string> Events { get; } = [];
        public Action<int>? Completed;
        public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken token)
        {
            LoadCount++;
            var view = await inner.LoadAsync(roomId, token);
            Events.Add($"loader-completed:{LoadCount}");
            Completed?.Invoke(LoadCount);
            return view;
        }
    }

    private sealed class ProposalRefreshEventLog(CountingLoader loader, RecordingFocusJsRuntime focusInterop)
    {
        private readonly List<string> events = [];
        private string? focusTargetId;
        private IRenderedComponent<RoomEditorV2Page>? page;
        private string? expectedReference;
        private string? expectedTransitionSource;

        public void Attach(IRenderedComponent<RoomEditorV2Page> renderedPage, string targetId, string reference, string transitionSource)
        {
            focusTargetId = targetId;
            page = renderedPage;
            expectedReference = reference;
            expectedTransitionSource = transitionSource;
            loader.Completed = loadCount =>
            {
                if (loadCount == 2) events.Add("loader completion");
            };
            focusInterop.FocusInvoked = RecordRenderedCompleteViewThenFocus;
        }

        private void RecordRenderedCompleteViewThenFocus(string targetId)
        {
            Assert.Equal(focusTargetId, targetId);
            Assert.Equal(new[] { "loader completion" }, events);

            // This JS-interoperability callback runs from the page's actual
            // OnAfterRender focus call. At that boundary, inspect the rendered
            // page before recording focus: both independently refreshed table
            // values must already be in markup and no focus call may exist.
            Assert.NotNull(page);
            Assert.Empty(page!.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(expectedReference, ReferenceInput(page).GetAttribute("value"));
            Assert.Equal(expectedTransitionSource, page.Find("input[aria-label='Transition source subroom reference']").GetAttribute("value"));
            Assert.Equal(0, focusInterop.FocusCallCount);
            events.Add("rendered complete view");
            events.Add("focusEditorField");
        }

        public void AssertExactOrder(string targetId)
        {
            Assert.Equal(new[] { "loader completion", "rendered complete view", "focusEditorField" }, events);
            Assert.Equal(1, focusInterop.FocusCalls(targetId));
            Assert.Equal(1, focusInterop.FocusCallCount);
        }
    }

    /// <summary>Test-only JS handler: it observes the actual interop call without changing page behavior.</summary>
    private sealed class RecordingFocusJsRuntime : IJSRuntime
    {
        public Action<string>? FocusInvoked { get; set; }
        private readonly List<string> focusTargets = [];
        public int FocusCallCount => focusTargets.Count;
        public int FocusCalls(string clientRowId, string field) => FocusCalls($"editor-row-{clientRowId}-{field}");
        public int FocusCalls(string targetId) => focusTargets.Count(x => x == targetId);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "focusEditorField")
            {
                var targetId = (string)args![0]!;
                FocusInvoked?.Invoke(targetId);
                focusTargets.Add(targetId);
            }
            if (typeof(TValue) == typeof(IJSObjectReference))
                return ValueTask.FromResult((TValue)(object)new NoopJsObjectReference());
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class NoopJsObjectReference : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
