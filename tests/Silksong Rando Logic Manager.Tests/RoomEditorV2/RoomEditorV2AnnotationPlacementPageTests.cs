using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>
/// Rendered migration-current SQLite coverage for typed page placement outcomes.
/// It does not exercise browser-runtime primary-gesture dispatch through Blazor's
/// DotNetObjectReference; that route remains manual Priority 4t acceptance.
/// </summary>
public sealed class RoomEditorV2AnnotationPlacementPageTests
{
    [Fact]
    public async Task RenderedActionBox_InvokesAllFiveActionsAndCancelsArmingBeforeEachImmediateMutation()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var transition = new RoomTransition
        {
            RoomId = room.Id, Alias = "exit", FriendlyName = "exit", Requirements = "r",
            EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5,
            InGamePositionX = 20, InGamePositionY = 30
        };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition); await db.SaveChangesAsync(); }

        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        page.Find($"#v2-transition-select-{transition.Id}").Click();
        page.WaitForAssertion(() => AssertActionAvailability(page, true, true, true, true, true));

        // Re-arm is transient; reset is immediate and must cancel it before its
        // real SQLite command/whole-room refresh. Selection survives the refresh.
        page.Find("[data-scene-action='rearm']").Click();
        page.WaitForAssertion(() => AssertActionAvailability(page, false, true, true, true, true));
        page.Find("[data-scene-action='reset']").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Null(Placement(page));
            Assert.Equal(new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, transition.Id), Selected(page));
            AssertActionAvailability(page, true, true, true, true, true);
        });
        await using (var verify = fixture.CreateDbContext())
        {
            var saved = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
            Assert.Equal((20d, 30d, true), (saved.AnnotationSceneUnitX, saved.AnnotationSceneUnitY, saved.EnableAnnotation));
        }

        // Hide/show and clear are also admitted while armed, and each cancels the
        // owner request before changing durable state.
        page.Find("[data-scene-action='rearm']").Click();
        page.Find("[data-scene-action='visibility']").Click();
        page.WaitForAssertion(() => { Assert.Null(Placement(page)); Assert.Equal("show annotation", page.Find("[data-scene-action='visibility']").GetAttribute("aria-label")); });
        page.Find("[data-scene-action='visibility']").Click();
        page.WaitForAssertion(() => Assert.Equal("hide annotation", page.Find("[data-scene-action='visibility']").GetAttribute("aria-label")));
        page.Find("[data-scene-action='rearm']").Click();
        page.Find("[data-scene-action='clear']").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Null(Placement(page));
            Assert.Equal(new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, transition.Id), Selected(page));
            AssertActionAvailability(page, true, true, false, false, true);
        });

        // Dismiss first cancels an arm while retaining identity, then dismisses
        // the unarmed pane-local selection. Neither path writes or refreshes.
        var loadsBeforeDismiss = loader.Loads;
        page.Find("[data-scene-action='rearm']").Click();
        page.Find("[data-scene-action='dismiss']").Click();
        page.WaitForAssertion(() => { Assert.Null(Placement(page)); Assert.NotNull(Selected(page)); });
        page.Find("[data-scene-action='dismiss']").Click();
        page.WaitForAssertion(() => { Assert.Null(Selected(page)); AssertActionAvailability(page, false, false, false, false, false); });
        Assert.Equal(loadsBeforeDismiss, loader.Loads);
    }

    [Fact]
    public async Task RenderedActionBox_ProvesSelectedArmedAndUnavailableAvailabilityMatrix()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var invalid = new RoomTransition { RoomId = room.Id, Alias = "invalid", FriendlyName = "invalid", Requirements = "r" };
        var valid = new RoomTransition { RoomId = room.Id, Alias = "valid", FriendlyName = "valid", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 3, AnnotationSceneUnitY = 4, InGamePositionX = 5, InGamePositionY = 6 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, invalid, valid); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));

        AssertActionAvailability(page, false, false, false, false, false); // no selection
        page.Find($"#v2-transition-place-{invalid.Id}").Click();
        page.WaitForAssertion(() => AssertActionAvailability(page, false, false, false, false, true)); // invalid/armed
        page.Find("[data-scene-action='dismiss']").Click();
        page.WaitForAssertion(() => AssertActionAvailability(page, true, false, false, false, true)); // invalid/unarmed
        page.Find("[data-scene-action='dismiss']").Click();
        page.Find($"#v2-transition-select-{valid.Id}").Click();
        page.WaitForAssertion(() => AssertActionAvailability(page, true, true, true, true, true)); // rendered/enabled
        page.Find("[data-scene-action='visibility']").Click();
        page.WaitForAssertion(() => AssertActionAvailability(page, true, true, true, true, true)); // valid/hidden

        // This is the page's transient owner-readiness gate (the same one that
        // rejects a stale/replaced browser owner); it leaves the typed route
        // rendered while every action is unavailable.
        typeof(RoomEditorV2Page).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page.Instance, true);
        page.Render();
        AssertActionAvailability(page, false, false, false, false, false); // unavailable
    }

    [Fact]
    public async Task RenderedActionBox_ExpectedFailureKeepsSelectionLocalStatusAndSameButtonRetry()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "exit", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5, InGamePositionX = 8, InGamePositionY = 9 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        page.Find($"#v2-transition-select-{transition.Id}").Click();
        await using (var concurrent = fixture.CreateDbContext())
        {
            var changed = await concurrent.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
            // The actual command re-reads imported game position for reset
            // admission. A concurrent correction makes this an ordinary expected
            // rejection without replacing the page command service.
            changed.InGamePositionX = null;
            changed.InGamePositionY = null;
            await concurrent.SaveChangesAsync();
        }

        await page.Find("[data-scene-action='reset']").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() =>
        {
            Assert.Equal(new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, transition.Id), Selected(page));
            Assert.Contains("no valid imported game position", page.Find("[data-scene-status-text='true']").TextContent, StringComparison.OrdinalIgnoreCase);
            Assert.False(page.Find("[data-scene-action='reset']").HasAttribute("disabled"));
        });
        var firstStatus = page.Find("[data-scene-status-text='true']").TextContent;
        await page.Find("[data-scene-action='reset']").ClickAsync(new MouseEventArgs()); // actual same rendered button retry
        page.WaitForAssertion(() => Assert.Equal(firstStatus, page.Find("[data-scene-status-text='true']").TextContent));
        Assert.Null(context.Services.GetRequiredService<DiagnosticState>().ErrorDetails);
    }

    [Fact]
    public async Task RenderedActionBox_UnexpectedSqliteFailureUsesGlobalDiagnosticAndRetainsPaneSelection()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "exit", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(parameters => parameters.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        page.Find($"#v2-transition-select-{transition.Id}").Click();
        await using (var broken = fixture.CreateDbContext())
            await broken.Database.ExecuteSqlRawAsync("DROP TABLE RoomTransitions;");

        await page.Find("[data-scene-action='clear']").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.NotNull(context.Services.GetRequiredService<DiagnosticState>().ErrorDetails));
        Assert.Equal(new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, transition.Id), Selected(page));
    }

    [Fact]
    public async Task RenderedActionBox_InFlightImmediateCommandDisablesEveryActionUntilCoordinatorCompletes()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "exit", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(parameters => parameters.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        page.Find($"#v2-transition-select-{transition.Id}").Click();
        var gate = (SemaphoreSlim)typeof(RoomEditorV2Page).GetField("commandGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!;
        await gate.WaitAsync();
        try
        {
            var click = page.Find("[data-scene-action='clear']").ClickAsync(new MouseEventArgs());
            page.WaitForAssertion(() => AssertActionAvailability(page, false, false, false, false, false));
            gate.Release();
            await click;
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
        page.WaitForAssertion(() => AssertActionAvailability(page, true, false, false, false, true));
    }

    [Fact]
    public async Task RenderedActionBox_ModalBlocksEveryImmediateAnnotationCommandBeforeAndAfterTheCommandGate()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var shown = new RoomTransition { RoomId = room.Id, Alias = "shown", FriendlyName = "shown", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5, InGamePositionX = 8, InGamePositionY = 9 };
        var hidden = new RoomTransition { RoomId = room.Id, Alias = "hidden", FriendlyName = "hidden", Requirements = "r", EnableAnnotation = false, AnnotationSceneUnitX = 6, AnnotationSceneUnitY = 7 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, shown, hidden); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        page.Find($"#v2-transition-select-{shown.Id}").Click();
        page.Find($"#v2-transition-metadata-{shown.Id}").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));

        await AssertImmediateActionsBlockedAsync(page, fixture, loader, room.Id, shown.Id, [V2SceneAction.Reset, V2SceneAction.Clear, V2SceneAction.ToggleVisibility]);

        // This invokes the typed page callback directly to cover the ordinary
        // page outcome; it is not browser-runtime owner dispatch.
        await SelectFromRenderedSceneOwnerAsync(page, room.Id, hidden.Id);
        await AssertImmediateActionsBlockedAsync(page, fixture, loader, room.Id, hidden.Id, [V2SceneAction.ToggleVisibility]);

        // A block appearing while the action waits for the coordinator gate
        // must not cancel an existing arm or admit a command.
        var openModal = typeof(RoomEditorV2Page).GetField("modalState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance);
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        page.Find($"#v2-transition-select-{shown.Id}").Click();
        page.Find("[data-scene-action='rearm']").Click();
        var gate = (SemaphoreSlim)typeof(RoomEditorV2Page).GetField("commandGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!;
        await gate.WaitAsync();
        try
        {
            var blocked = InvokeRenderedActionAsync(page, V2SceneAction.Clear);
            page.WaitForAssertion(() => AssertActionAvailability(page, false, false, false, false, false));
            typeof(RoomEditorV2Page).GetField("modalState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page.Instance, openModal);
            gate.Release();
            await blocked;
        }
        finally { if (gate.CurrentCount == 0) gate.Release(); }
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        Assert.Equal(new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, shown.Id), Selected(page));
        Assert.NotNull(Placement(page));
    }

    [Fact]
    public async Task RenderedActionBox_MeaningfulPendingChildDraftBlocksEveryImmediateAnnotationCommand()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var shown = new RoomTransition { RoomId = room.Id, Alias = "shown", FriendlyName = "shown", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5, InGamePositionX = 8, InGamePositionY = 9 };
        var hidden = new RoomTransition { RoomId = room.Id, Alias = "hidden", FriendlyName = "hidden", Requirements = "r", EnableAnnotation = false, AnnotationSceneUnitX = 6, AnnotationSceneUnitY = 7 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, shown, hidden); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        page.Find($"#v2-transition-select-{shown.Id}").Click();
        await SetMeaningfulPendingCheckDraftAsync(page);

        await AssertImmediateActionsBlockedAsync(page, fixture, loader, room.Id, shown.Id, [V2SceneAction.Reset, V2SceneAction.Clear, V2SceneAction.ToggleVisibility]);
        await SelectFromRenderedSceneOwnerAsync(page, room.Id, hidden.Id);
        await SetMeaningfulPendingCheckDraftAsync(page);
        await AssertImmediateActionsBlockedAsync(page, fixture, loader, room.Id, hidden.Id, [V2SceneAction.ToggleVisibility]);
    }

    private static async Task AssertImmediateActionsBlockedAsync(IRenderedComponent<RoomEditorV2Page> page, MigratedSqliteFixture fixture, CountingLoader loader, Guid roomId, Guid selectedId, IReadOnlyList<V2SceneAction> actions)
    {
        var beforeLoads = loader.Loads; var beforeTrace = Trace(page).Events.Count; var beforeScene = Trace(page).SceneLoaderInvocations; var beforeMap = Trace(page).MapLoaderInvocations;
        var beforeSelection = Selected(page); var beforePlacement = Placement(page);
        await using var beforeDb = fixture.CreateDbContext();
        var before = await beforeDb.RoomTransitions.Where(x => x.RoomId == roomId).OrderBy(x => x.Id).Select(x => new { x.Id, x.EnableAnnotation, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.UpdatedUtc }).ToArrayAsync();
        foreach (var action in actions)
        {
            var button = page.Find($"[data-scene-action='{ActionName(action)}']");
            Assert.True(button.HasAttribute("disabled"));
            await InvokeRenderedActionAsync(page, action);
        }
        Assert.Equal(beforeSelection, Selected(page)); Assert.Equal(beforePlacement, Placement(page));
        Assert.Equal(beforeLoads, loader.Loads); Assert.Equal(beforeTrace, Trace(page).Events.Count); Assert.Equal(beforeScene, Trace(page).SceneLoaderInvocations); Assert.Equal(beforeMap, Trace(page).MapLoaderInvocations);
        await using var afterDb = fixture.CreateDbContext();
        var after = await afterDb.RoomTransitions.Where(x => x.RoomId == roomId).OrderBy(x => x.Id).Select(x => new { x.Id, x.EnableAnnotation, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.UpdatedUtc }).ToArrayAsync();
        Assert.Equal(before, after);
        Assert.Equal(selectedId, Selected(page)!.EntityId);
    }

    private static async Task SelectFromRenderedSceneOwnerAsync(IRenderedComponent<RoomEditorV2Page> page, Guid roomId, Guid entityId)
    {
        var pane = page.FindComponent<SceneContextPanePresentation>().Instance;
        var generation = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)!;
        await pane.SelectSceneItemAsync(roomId.ToString(), generation, "exit", entityId.ToString());
        page.WaitForAssertion(() => Assert.Equal(entityId, Selected(page)!.EntityId));
    }

    private static async Task SetMeaningfulPendingCheckDraftAsync(IRenderedComponent<RoomEditorV2Page> page)
    {
        var table = page.FindComponent<CheckTablePresentation>().Instance;
        var rows = (System.Collections.IList)typeof(CheckTablePresentation).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
        var tail = rows[rows.Count - 1]!;
        var change = typeof(CheckTablePresentation).GetMethod("TextChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)change.Invoke(table, [tail, "name", new ChangeEventArgs { Value = "pending check" }])!;
        page.Render();
        page.WaitForAssertion(() => Assert.True((bool)typeof(RoomEditorV2Page).GetField("meaningfulPendingDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!));
    }

    private static Task InvokeRenderedActionAsync(IRenderedComponent<RoomEditorV2Page> page, V2SceneAction action)
    {
        var pane = page.FindComponent<SceneContextPanePresentation>().Instance;
        var generation = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)!;
        return pane.ActionRequested!(page.Instance.RoomId, generation, action);
    }

    private static string ActionName(V2SceneAction action) => action switch { V2SceneAction.Reset => "reset", V2SceneAction.Clear => "clear", V2SceneAction.ToggleVisibility => "visibility", _ => throw new ArgumentOutOfRangeException(nameof(action)) };

    private static void AssertActionAvailability(IRenderedComponent<RoomEditorV2Page> page, params bool[] enabled)
    {
        var buttons = page.Find("[data-scene-annotation-action-box='true']").QuerySelectorAll("button");
        Assert.Equal(5, buttons.Length);
        Assert.Equal(enabled, buttons.Select(button => !button.HasAttribute("disabled")));
    }
    private static V2SceneSelectedItem? Selected(IRenderedComponent<RoomEditorV2Page> page) =>
        (V2SceneSelectedItem?)typeof(RoomEditorV2Page).GetField("selectedSceneItem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance);
    private static SceneAnnotationPlacementRequest? Placement(IRenderedComponent<RoomEditorV2Page> page) =>
        (SceneAnnotationPlacementRequest?)typeof(RoomEditorV2Page).GetField("placementRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance);

    [Theory]
    [InlineData("transition")]
    [InlineData("check")]
    public async Task ActiveRowPlacementAction_ArmsThenCommitsThroughSceneCallback_WithOneRefreshAndOneSceneLoad(string kind)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));

        // Both active persisted row types must expose the action, including the
        // disabled/unplaced check used by the check route below.
        Assert.False(page.Find($"#v2-transition-place-{seed.Transition.Id}").HasAttribute("disabled"));
        Assert.False(page.Find($"#v2-check-place-{seed.Check.Id}").HasAttribute("disabled"));

        var entityId = kind == "transition" ? seed.Transition.Id : seed.Check.Id;
        var actionId = $"v2-{kind}-place-{entityId}";
        var beforeTrace = Trace(page).Events.Count;
        var beforeSceneLoads = Trace(page).SceneLoaderInvocations;
        var beforeLoads = loader.Loads;
        await using var beforeDb = fixture.CreateDbContext();
        var beforeUpdatedUtc = kind == "transition"
            ? (await beforeDb.RoomTransitions.SingleAsync(x => x.Id == entityId)).UpdatedUtc
            : (await beforeDb.CheckLocations.SingleAsync(x => x.Id == entityId)).UpdatedUtc;

        page.Find($"#{actionId}").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal("true", page.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-placement-armed"));
            Assert.Contains($"place {(kind == "transition" ? "exit" : "check")} annotation", page.Find("[data-scene-status-text='true']").TextContent);
            Assert.Equal("re-arm placement", page.Find($"#{actionId}").GetAttribute("title"));
            Assert.Equal("re-arm placement", page.Find($"#{actionId}").GetAttribute("aria-label"));
            Assert.Equal(beforeLoads, loader.Loads);
            Assert.Equal(beforeSceneLoads, Trace(page).SceneLoaderInvocations);
        });
        await using (var armedDb = fixture.CreateDbContext())
        {
            var armedUpdatedUtc = kind == "transition"
                ? (await armedDb.RoomTransitions.SingleAsync(x => x.Id == entityId)).UpdatedUtc
                : (await armedDb.CheckLocations.SingleAsync(x => x.Id == entityId)).UpdatedUtc;
            Assert.Equal(beforeUpdatedUtc, armedUpdatedUtc);
        }

        // Invoke the typed page callback directly. Browser-runtime primary-click
        // dispatch is intentionally manual acceptance, not this component test.
        await page.InvokeAsync(() => page.FindComponent<SceneContextPanePresentation>().Instance.CommitPlacementAsync(kind == "transition" ? "exit" : "check", entityId.ToString(), 20.25, 30.5));

        page.WaitForAssertion(() =>
        {
            Assert.Equal(beforeLoads + 1, loader.Loads);
            Assert.Equal(beforeSceneLoads + 1, Trace(page).SceneLoaderInvocations);
            Assert.Equal(0, Trace(page).MapLoaderInvocations);
            Assert.Equal(beforeTrace + 3, Trace(page).Events.Count);
            Assert.Equal(new[]
            {
                $"{kind}-annotation-placement:committed",
                "scene-load:scene-load",
                $"{kind}-annotation-placement:complete-room-refresh"
            }, Trace(page).Events.Skip(beforeTrace));
            Assert.Single(page.FindAll($"[data-scene-id='{entityId}'][data-scene-selection-kind='{(kind == "transition" ? "exit" : "check")}']"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "selectV2SceneLayoutItem" && (string)invocation.Arguments[1]! == entityId.ToString("D"));
        });

        await using var verify = fixture.CreateDbContext();
        if (kind == "transition")
        {
            var saved = await verify.RoomTransitions.SingleAsync(x => x.Id == entityId);
            Assert.Equal((20.25d, 30.5d), (saved.AnnotationSceneUnitX, saved.AnnotationSceneUnitY));
            Assert.Equal(("a", "authored exit", "requirements", "notes", "game-exit", (double?)null, (double?)null, 3d, 4d, 5d, 6d),
                (saved.Alias, saved.FriendlyName, saved.Requirements, saved.Notes, saved.InGameId, saved.InGamePositionX, saved.InGamePositionY, saved.InGamePositionZ, saved.LocalPositionX, saved.LocalPositionY, saved.LocalPositionZ));
        }
        else
        {
            var saved = await verify.CheckLocations.SingleAsync(x => x.Id == entityId);
            Assert.True(saved.EnableAnnotation);
            Assert.Equal((20.25d, 30.5d), (saved.AnnotationSceneUnitX, saved.AnnotationSceneUnitY));
            Assert.Equal(("authored check", "requirements", "notes", "game-check", (double?)null, (double?)null, 9d, 10d, 11d, 12d),
                (saved.FriendlyName, saved.Requirements, saved.Notes, saved.InGameId, saved.InGamePositionX, saved.InGamePositionY, saved.InGamePositionZ, saved.LocalPositionX, saved.LocalPositionY, saved.LocalPositionZ));
        }
    }

    [Fact]
    public async Task ReplacedSameRoomPlacementCallbacks_AreRejectedBeforeArmMutationOrDurableWrite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var pane = page.FindComponent<SceneContextPanePresentation>();
        var generation = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane.Instance)!;
        var selected = (V2SceneSelectedItem?)typeof(RoomEditorV2Page).GetField("selectedSceneItem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance);
        var placement = typeof(RoomEditorV2Page).GetField("placementRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;

        page.Find($"#v2-transition-place-{seed.Transition.Id}").Click();
        page.WaitForAssertion(() => Assert.NotNull(placement.GetValue(page.Instance)));
        var armed = Assert.IsType<SceneAnnotationPlacementRequest>(placement.GetValue(page.Instance));
        selected = Assert.IsType<V2SceneSelectedItem>(typeof(RoomEditorV2Page).GetField("selectedSceneItem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance));
        var beforeLoads = loader.Loads;
        await using var beforeDb = fixture.CreateDbContext();
        var before = await beforeDb.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id);
        var durableBefore = (before.UpdatedUtc, before.EnableAnnotation, before.AnnotationSceneUnitX, before.AnnotationSceneUnitY);

        // The page has accepted a newer same-room mounted pane. This older pane's
        // callback still has an equal request, but no longer owns the page.
        await pane.Instance.OwnerMounted!(seed.Room.Id, generation + 1);
        await page.InvokeAsync(() => pane.Instance.CancelPlacementAsync(seed.Room.Id.ToString(), generation));
        await page.InvokeAsync(() => pane.Instance.CommitPlacementAsync(seed.Room.Id.ToString(), generation, "exit", seed.Transition.Id.ToString(), 20.25, 30.5));
        // This is the old rendered owner's actual action-box event. Its typed
        // room/generation contract must be rejected just like its JS callbacks.
        await pane.Find("[data-scene-action='dismiss']").ClickAsync(new MouseEventArgs());

        Assert.Equal(armed, placement.GetValue(page.Instance));
        Assert.Equal(selected, typeof(RoomEditorV2Page).GetField("selectedSceneItem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance));
        Assert.Equal(beforeLoads, loader.Loads);
        await using var afterDb = fixture.CreateDbContext();
        var after = await afterDb.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id);
        Assert.Equal(durableBefore, (after.UpdatedUtc, after.EnableAnnotation, after.AnnotationSceneUnitX, after.AnnotationSceneUnitY));
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("check")]
    public async Task RenderedAnnotationActions_SelectShownAndShowHidden_WithBranchSpecificAccessibility(string kind)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedShowAndRemoveAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var showId = kind == "transition" ? seed.TransitionShow.Id : seed.CheckShow.Id;
        var shownId = kind == "transition" ? seed.TransitionRemove.Id : seed.CheckRemove.Id;
        var prefix = $"v2-{kind}";

        var show = page.Find($"#{prefix}-show-{showId}");
        var select = page.Find($"#{prefix}-select-{shownId}");
        Assert.False(show.HasAttribute("disabled"));
        Assert.False(select.HasAttribute("disabled"));
        Assert.Equal("show and select annotation", show.GetAttribute("title"));
        Assert.Equal("show and select annotation", show.GetAttribute("aria-label"));
        Assert.Equal("select annotation", select.GetAttribute("title"));
        Assert.Equal("select annotation", select.GetAttribute("aria-label"));
        Assert.Equal("true", select.QuerySelector(".annotation-crosshairs")!.GetAttribute("aria-hidden"));
        var beforeLoads = loader.Loads; var beforeScene = Trace(page).SceneLoaderInvocations;
        show.Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal(beforeLoads + 1, loader.Loads);
            Assert.Equal(beforeScene + 1, Trace(page).SceneLoaderInvocations);
            Assert.Equal(0, Trace(page).MapLoaderInvocations);
            Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "selectV2SceneLayoutItem" && (string)x.Arguments[1]! == showId.ToString("D"));
        });

        var beforeSelectLoads = loader.Loads;
        var beforeSelectScene = Trace(page).SceneLoaderInvocations;
        select.Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal(beforeSelectLoads, loader.Loads);
            Assert.Equal(beforeSelectScene, Trace(page).SceneLoaderInvocations);
            Assert.Equal(0, Trace(page).MapLoaderInvocations);
            Assert.Single(page.FindAll($"[data-scene-id='{shownId}']"));
            Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "selectV2SceneLayoutItem" && (string)x.Arguments[1]! == shownId.ToString("D"));
        });
        await using var verify = fixture.CreateDbContext();
        if (kind == "transition")
        {
            Assert.True((await verify.RoomTransitions.SingleAsync(x => x.Id == showId)).EnableAnnotation);
            Assert.True((await verify.RoomTransitions.SingleAsync(x => x.Id == shownId)).EnableAnnotation);
        }
        else
        {
            Assert.True((await verify.CheckLocations.SingleAsync(x => x.Id == showId)).EnableAnnotation);
            Assert.True((await verify.CheckLocations.SingleAsync(x => x.Id == shownId)).EnableAnnotation);
        }
    }

    [Fact]
    public async Task EnabledUnplacedRows_RenderPlacementRatherThanRemovalAndArmWithoutWriting()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var source = new Subroom { RoomId = room.Id, FriendlyName = "source", ReferenceId = "source" };
        var destination = new Subroom { RoomId = room.Id, FriendlyName = "destination", ReferenceId = "destination" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r" };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r" };
        var connection = new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, source, destination, transition, check, connection); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }

        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        foreach (var (kind, id) in new[] { ("transition", transition.Id), ("check", check.Id), ("connection", connection.Id) })
        {
            Assert.Empty(page.FindAll($"#v2-{kind}-remove-{id}"));
            var action = page.Find($"#v2-{kind}-place-{id}");
            Assert.False(action.HasAttribute("disabled"));
            action.Click();
            page.WaitForAssertion(() => Assert.Equal("true", page.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-placement-armed")));
            await page.InvokeAsync(() => page.FindComponent<SceneContextPanePresentation>().Instance.CancelPlacementAsync());
        }

        await using var verify = fixture.CreateDbContext();
        Assert.All(await verify.RoomTransitions.Where(x => x.Id == transition.Id).ToArrayAsync(), row => { Assert.True(row.EnableAnnotation); Assert.Null(row.AnnotationSceneUnitX); Assert.Null(row.AnnotationSceneUnitY); });
        Assert.All(await verify.CheckLocations.Where(x => x.Id == check.Id).ToArrayAsync(), row => { Assert.True(row.EnableAnnotation); Assert.Null(row.AnnotationSceneUnitX); Assert.Null(row.AnnotationSceneUnitY); });
        Assert.All(await verify.SubroomConnections.Where(x => x.Id == connection.Id).ToArrayAsync(), row => { Assert.True(row.EnableAnnotation); Assert.Null(row.SceneUnitX); Assert.Null(row.SceneUnitY); });
    }

    [Fact]
    public async Task SceneUnavailableSlotTwoUsesTheSuppliedReasonAndDoesNotInvokeAnnotationCommands()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedConnectionAsync(fixture, bidirectional: false);
        var transition = new RoomTransition { RoomId = seed.Room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 1, AnnotationSceneUnitY = 2 };
        await using (var db = fixture.CreateDbContext()) { db.Add(transition); await db.SaveChangesAsync(); }
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var beforeLoads = loader.Loads;
        var beforeSceneLoads = Trace(page).SceneLoaderInvocations;
        typeof(RoomEditorV2Page).GetField("showMapContext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page.Instance, false);
        page.Render();

        const string reason = "Scene annotation actions are unavailable because map context is hidden.";
        foreach (var selector in new[] { $"#v2-transition-select-{transition.Id}", $"#v2-connection-place-{seed.First.Id}" })
        {
            var action = page.Find(selector);
            Assert.True(action.HasAttribute("disabled"));
            Assert.Equal(reason, action.GetAttribute("title"));
            Assert.Equal(reason, action.GetAttribute("aria-label"));
        }
        Assert.Equal(beforeLoads + 1, loader.Loads);
        Assert.Equal(beforeSceneLoads + 1, Trace(page).SceneLoaderInvocations);
    }

    [Fact]
    public async Task RenderedConnectionAction_ArmsNoncanonicalEqualOrderExactBidirectionalGroupAndSelectsLowestIdMarker()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedConnectionAsync(fixture, bidirectional: true, equalSortOrderDeterministicIds: true);
        Assert.True(seed.First.Id.CompareTo(seed.Second!.Id) < 0);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.False(page.Find($"#v2-connection-place-{seed.First.Id}").HasAttribute("disabled")));
        Assert.False(page.Find($"#v2-connection-place-{seed.Second!.Id}").HasAttribute("disabled"));
        var before = Trace(page).Events.Count;
        var loads = loader.Loads; var scenes = Trace(page).SceneLoaderInvocations;
        page.Find($"#v2-connection-place-{seed.Second.Id}").Click();
        await page.InvokeAsync(() => page.FindComponent<SceneContextPanePresentation>().Instance.CommitPlacementAsync("connection", seed.Second.Id.ToString(), 20.25, 30.5));
        page.WaitForAssertion(() =>
        {
            Assert.Equal(loads + 1, loader.Loads);
            Assert.Equal(new[] { "connection-annotation-placement:committed", "scene-load:scene-load", "connection-annotation-placement:complete-room-refresh" }, Trace(page).Events.Skip(before));
            Assert.Equal(scenes + 1, Trace(page).SceneLoaderInvocations);
            Assert.Equal(0, Trace(page).MapLoaderInvocations);
            var marker = Assert.Single(page.FindAll("[data-scene-layout-marker='connection']"));
            Assert.Equal(seed.First.Id.ToString(), marker.GetAttribute("data-scene-id"));
            Assert.Single(context.JSInterop.Invocations, x => x.Identifier == "selectV2SceneLayoutItem" && (string)x.Arguments[1]! == seed.First.Id.ToString("D"));
        });
        await using var db = fixture.CreateDbContext();
        var rows = await db.SubroomConnections.Where(x => x.RoomId == seed.Room.Id).OrderBy(x => x.SortOrder).ToArrayAsync();
        Assert.All(rows, row => { Assert.True(row.EnableAnnotation); Assert.Equal((20.25d, 30.5d), (row.SceneUnitX, row.SceneUnitY)); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedConnectionAction_ArmsAndCommitsOneWayOrValidUnresolvedGroup(bool unresolved)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedConnectionAsync(fixture, bidirectional: false, unresolved);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        var actionId = $"v2-connection-place-{seed.First.Id}";
        page.WaitForAssertion(() => Assert.False(page.Find($"#{actionId}").HasAttribute("disabled")));
        var loads = loader.Loads; var scenes = Trace(page).SceneLoaderInvocations; var trace = Trace(page).Events.Count;
        page.Find($"#{actionId}").Click();
        await page.InvokeAsync(() => page.FindComponent<SceneContextPanePresentation>().Instance.CommitPlacementAsync("connection", seed.First.Id.ToString(), 12.5, 25.5));
        page.WaitForAssertion(() =>
        {
            Assert.Equal(loads + 1, loader.Loads); Assert.Equal(scenes + 1, Trace(page).SceneLoaderInvocations); Assert.Equal(0, Trace(page).MapLoaderInvocations);
            Assert.Equal(new[] { "connection-annotation-placement:committed", "scene-load:scene-load", "connection-annotation-placement:complete-room-refresh" }, Trace(page).Events.Skip(trace));
            Assert.Single(page.FindAll($"[data-scene-layout-marker='connection'][data-scene-id='{seed.First.Id}']"));
            Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "selectV2SceneLayoutItem" && (string)x.Arguments[1]! == seed.First.Id.ToString("D"));
        });
        await using var db = fixture.CreateDbContext();
        var saved = await db.SubroomConnections.SingleAsync(x => x.Id == seed.First.Id);
        Assert.True(saved.EnableAnnotation); Assert.Equal((12.5d, 25.5d), (saved.SceneUnitX, saved.SceneUnitY));
    }

    [Theory]
    [InlineData("blank-alias")]
    [InlineData("invalid-alias")]
    [InlineData("missing-name")]
    [InlineData("malformed")]
    [InlineData("no-active-subroom")]
    public async Task RenderedIneligibleConnectionGroups_RemainVisibleDisabledAndYellowWithoutMutation(string state)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedIneligibleConnectionAsync(fixture, state);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() =>
        {
            foreach (var row in seed.Rows.Where(x => !x.IsArchived))
            {
                var action = page.Find($"#v2-connection-place-{row.Id}");
                Assert.True(action.HasAttribute("disabled"));
                Assert.Equal("Connection annotation is unavailable because this connection group is ineligible.", action.GetAttribute("title"));
                Assert.Equal("Connection annotation is unavailable because this connection group is ineligible.", action.GetAttribute("aria-label"));
                Assert.Equal("true", action.QuerySelector(".annotation-crosshairs")!.GetAttribute("aria-hidden"));
                Assert.Single(action.QuerySelectorAll(".annotation-crosshairs-warning .fa-crosshairs"));
            }
        });
        Assert.Equal(1, loader.Loads); Assert.Equal(1, Trace(page).SceneLoaderInvocations); Assert.Equal(0, Trace(page).MapLoaderInvocations);
        await using var db = fixture.CreateDbContext();
        var rows = await db.SubroomConnections.Where(x => x.RoomId == seed.Room.Id).OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(seed.Rows.Select(x => (x.Id, x.UpdatedUtc, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY)).OrderBy(x => x.Id), rows.Select(x => (x.Id, x.UpdatedUtc, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY)).OrderBy(x => x.Id));
    }

    [Fact]
    public async Task RenderedDisabledMixedCoordinateConnectionGroup_IsYellowAndRearmsWithoutShowCommandOrRefresh()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedConnectionAsync(fixture, bidirectional: true);
        await using (var db = fixture.CreateDbContext())
        {
            var rows = await db.SubroomConnections.Where(x => x.RoomId == seed.Room.Id).OrderBy(x => x.SortOrder).ToArrayAsync();
            rows[0].SceneUnitX = 12.5;
            rows[0].SceneUnitY = 25.5;
            // A group action may not treat the complete sibling as a hidden valid
            // marker while another active same-alias row has only a partial pair.
            rows[1].SceneUnitX = 12.5;
            rows[1].SceneUnitY = null;
            await db.SaveChangesAsync();
        }

        var beforeRows = await ConnectionPersistenceAsync(fixture, seed.Room.Id);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var beforeLoads = loader.Loads;
        var beforeSceneLoads = Trace(page).SceneLoaderInvocations;
        var beforeTrace = Trace(page).Events.Count;

        foreach (var row in seed.Second is null ? new[] { seed.First } : new[] { seed.First, seed.Second! })
        {
            var action = page.Find($"#v2-connection-place-{row.Id}");
            Assert.False(action.HasAttribute("disabled"));
            Assert.Equal("re-arm placement", action.GetAttribute("title"));
            Assert.Equal("re-arm placement", action.GetAttribute("aria-label"));
            Assert.Equal("true", action.QuerySelector(".annotation-crosshairs")!.GetAttribute("aria-hidden"));
            Assert.Single(action.QuerySelectorAll(".annotation-crosshairs-warning .fa-crosshairs"));
        }

        page.Find($"#v2-connection-place-{seed.First.Id}").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal("true", page.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-placement-armed"));
            Assert.Contains("place connection annotation", page.Find("[data-scene-status-text='true']").TextContent);
            Assert.Equal(beforeLoads, loader.Loads);
            Assert.Equal(beforeSceneLoads, Trace(page).SceneLoaderInvocations);
            Assert.Equal(beforeTrace, Trace(page).Events.Count);
        });
        Assert.Equal(beforeRows, await ConnectionPersistenceAsync(fixture, seed.Room.Id));
    }

    [Fact]
    public async Task StaleConnectionArmExpectedRejection_ClearsArmShowsOutcomeAndFocusesInitiatorOnceWithoutRefresh()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedConnectionAsync(fixture, bidirectional: false);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        var actionId = $"v2-connection-place-{seed.First.Id}";
        page.WaitForAssertion(() => Assert.NotNull(page.Find($"#{actionId}")));
        var beforeTrace = Trace(page).Events.Count; var beforeLoads = loader.Loads; var beforeScene = Trace(page).SceneLoaderInvocations;
        page.Find($"#{actionId}").Click();
        await new LogicCatalogService(fixture).PlaceConnectionAnnotationAsync(seed.First.Id, 1, 2);
        await page.InvokeAsync(() => page.FindComponent<SceneContextPanePresentation>().Instance.CommitPlacementAsync("connection", seed.First.Id.ToString(), 20.25, 30.5));
        page.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("place connection annotation", page.Find("[data-scene-status-text='true']").TextContent);
            Assert.Contains("already enabled", page.Find(".connection-table-presentation [role='status']").TextContent, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeLoads, loader.Loads); Assert.Equal(beforeScene, Trace(page).SceneLoaderInvocations); Assert.Equal(0, Trace(page).MapLoaderInvocations); Assert.Equal(beforeTrace, Trace(page).Events.Count);
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == actionId));
        });
        await using var db = fixture.CreateDbContext(); var row = await db.SubroomConnections.SingleAsync(x => x.Id == seed.First.Id);
        Assert.Equal((1d, 2d), (row.SceneUnitX, row.SceneUnitY));
    }

    [Fact]
    public async Task RenderedEnabledConnectionAction_SelectsShownAnnotationWithoutMutation()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedConnectionAsync(fixture, bidirectional: true);
        await new LogicCatalogService(fixture).PlaceConnectionAnnotationAsync(seed.First.Id, 12.5, 25.5);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        var actionId = $"v2-connection-select-{seed.First.Id}";
        page.WaitForAssertion(() => Assert.False(page.Find($"#{actionId}").HasAttribute("disabled")));
        var beforeLoads = loader.Loads; var beforeScene = Trace(page).SceneLoaderInvocations; var beforeTrace = Trace(page).Events.Count;
        var action = page.Find($"#{actionId}");
        Assert.Equal("select annotation", action.GetAttribute("title"));
        Assert.Equal("select annotation", action.GetAttribute("aria-label"));
        Assert.Equal("true", action.QuerySelector(".annotation-crosshairs")!.GetAttribute("aria-hidden"));
        var beforeRows = await ConnectionPersistenceAsync(fixture, seed.Room.Id);
        action.Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal(beforeLoads, loader.Loads); Assert.Equal(beforeScene, Trace(page).SceneLoaderInvocations); Assert.Equal(0, Trace(page).MapLoaderInvocations);
            Assert.Equal(beforeTrace, Trace(page).Events.Count);
            Assert.Single(page.FindAll("[data-scene-layout-marker='connection']"));
            Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "selectV2SceneLayoutItem" && (string)invocation.Arguments[1]! == seed.First.Id.ToString("D"));
        });
        Assert.Equal(beforeRows, await ConnectionPersistenceAsync(fixture, seed.Room.Id));
    }

    [Fact]
    public async Task RenderedEnabledInvalidOrMalformedActiveConnectionGroup_ExposesNoRemoveActionAndInspectionDoesNotMutateSqlite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = await SeedEnabledRemoveIneligibleConnectionAsync(fixture, "malformed");
        var before = await ConnectionPersistenceAsync(fixture, room.Id);
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));

        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<ConnectionTablePresentation>()));
        Assert.Empty(page.FindAll("[id^='v2-connection-remove-']"));
        Assert.Equal(before, await ConnectionPersistenceAsync(fixture, room.Id));
    }

    [Fact]
    public async Task RenderedEnabledArchivedConnectionGroup_ExposesNoRemoveActionAndInspectionDoesNotMutateSqlite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = await SeedEnabledRemoveIneligibleConnectionAsync(fixture, "archived");
        var before = await ConnectionPersistenceAsync(fixture, room.Id);
        Assert.True(before.Single().EnableAnnotation);
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));

        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<ConnectionTablePresentation>()));
        page.FindAll("button").Single(button => button.TextContent.Trim() == "show archived room contents").Click();
        page.WaitForAssertion(() =>
        {
            Assert.NotNull(page.Find("table[data-v2-connection-table='archived']"));
            Assert.NotNull(page.Find($"tr[data-v2-connection-row='{before.Single().Id}']"));
        });
        Assert.Empty(page.FindAll("[id^='v2-connection-remove-']"));
        Assert.Equal(before, await ConnectionPersistenceAsync(fixture, room.Id));
    }

    [Fact]
    public async Task RenderedEnabledNoActiveSubroomConnectionGroup_ExposesNoRemoveActionAndInspectionDoesNotMutateSqlite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = await SeedEnabledRemoveIneligibleConnectionAsync(fixture, "no-active-subroom");
        var before = await ConnectionPersistenceAsync(fixture, room.Id);
        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));

        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<ConnectionTablePresentation>()));
        Assert.Empty(page.FindAll("[id^='v2-connection-remove-']"));
        Assert.Equal(before, await ConnectionPersistenceAsync(fixture, room.Id));
    }

    [Fact]
    public async Task UnifiedFourTableSlots_RenderExactActiveArchivedAndTailContracts_IndependentOfAnnotationLayerVisibility()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var activeSubroom = new Subroom { RoomId = room.Id, FriendlyName = "active subroom", ReferenceId = "a", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4 };
        var archivedSubroom = new Subroom { RoomId = room.Id, FriendlyName = "archived subroom", ReferenceId = "old-a", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5 };
        var archivedTransition = new RoomTransition { RoomId = room.Id, Alias = "x", FriendlyName = "old exit", Requirements = "r", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 6, AnnotationSceneUnitY = 7 };
        var archivedCheck = new CheckLocation { RoomId = room.Id, FriendlyName = "old check", Requirements = "r", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        var connection = new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "a", Requirements = "r", EnableAnnotation = true, SceneUnitX = 8, SceneUnitY = 9 };
        var archivedConnection = new SubroomConnection { RoomId = room.Id, Alias = "old-c", FriendlyName = "old connection", SourceSubroomReferenceText = "old-a", DestinationSubroomReferenceText = "old-a", Requirements = "r", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, activeSubroom, archivedSubroom, transition, archivedTransition, check, archivedCheck, connection, archivedConnection); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }

        using var context = PageContext(fixture, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));

        AssertActiveSlots(page, "subroom", activeSubroom.Id, "edit scene rectangle", "select annotation");
        AssertActiveSlots(page, "transition", transition.Id, "edit in-game data", "select annotation");
        AssertActiveSlots(page, "connection", connection.Id, null, "select annotation");
        AssertActiveSlots(page, "check", check.Id, "edit in-game data", "select annotation");
        Assert.All(page.FindAll("tr[data-v2-subroom-tail], tr[data-v2-transition-tail], tr[data-v2-connection-tail], tr[data-v2-check-tail] .row-actions"), tail => Assert.Empty(tail.QuerySelectorAll("button")));

        var transitionAction = page.Find($"#v2-transition-select-{transition.Id}");
        var title = transitionAction.GetAttribute("title"); var label = transitionAction.GetAttribute("aria-label");
        Assert.Equal("Hide annotations", page.Find("button[data-scene-annotation-toggle='true']").GetAttribute("aria-label"));
        // The layer control is browser-owned; its presence must not become a table
        // command or alter the table's durable renderability presentation.
        Assert.Equal(title, page.Find($"#v2-transition-select-{transition.Id}").GetAttribute("title"));
        Assert.Equal(label, page.Find($"#v2-transition-select-{transition.Id}").GetAttribute("aria-label"));
        Assert.Single(page.Find($"#v2-transition-select-{transition.Id}").QuerySelectorAll(".annotation-crosshairs .fa-crosshairs"));

        page.FindAll("button").Single(button => button.TextContent.Trim() == "show archived room contents").Click();
        page.WaitForAssertion(() =>
        {
            AssertArchivedSlots(page, "subroom", archivedSubroom.Id);
            AssertArchivedSlots(page, "transition", archivedTransition.Id);
            AssertArchivedSlots(page, "connection", archivedConnection.Id);
            AssertArchivedSlots(page, "check", archivedCheck.Id);
        });
    }

    private static void AssertActiveSlots(IRenderedComponent<RoomEditorV2Page> page, string kind, Guid id, string? supplemental, string annotation)
    {
        var actions = page.Find($"tr[data-v2-{kind}-row='{id}'] .row-actions");
        Assert.Equal(3, actions.QuerySelectorAll(".action-slot").Length);
        var slots = actions.QuerySelectorAll(".action-slot");
        if (supplemental is null) Assert.Empty(slots[0].QuerySelectorAll("button"));
        else Assert.Equal(supplemental, slots[0].QuerySelector("button")!.GetAttribute("aria-label"));
        Assert.Equal(annotation, slots[1].QuerySelector("button")!.GetAttribute("aria-label"));
        Assert.Single(slots[1].QuerySelectorAll(".annotation-crosshairs .fa-crosshairs"));
        Assert.Equal("archive", slots[2].QuerySelector("button")!.GetAttribute("aria-label"));
    }

    private static void AssertArchivedSlots(IRenderedComponent<RoomEditorV2Page> page, string kind, Guid id)
    {
        var actions = page.Find($"table[data-v2-{kind}-table='archived'] tr[data-v2-{kind}-row='{id}'] .row-actions");
        var slots = actions.QuerySelectorAll(".action-slot");
        Assert.Equal(3, slots.Length); Assert.Empty(slots[0].QuerySelectorAll("button"));
        Assert.Equal("restore", slots[1].QuerySelector("button")!.GetAttribute("aria-label"));
        Assert.Equal("delete permanently", slots[2].QuerySelector("button")!.GetAttribute("aria-label"));
    }

    private static TestContext PageContext(MigratedSqliteFixture fixture, out CountingLoader loader)
    {
        var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility").SetResult(true);
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>();
        context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>();
        context.Services.AddSingleton<MapLinkService>();
        var scene = new SceneLayoutLoader(fixture);
        loader = new(new RoomEditorV2LogicLoader(fixture, scene), scene);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        return context;
    }

    private static V2RoomOperationTrace Trace(IRenderedComponent<RoomEditorV2Page> page) => ((RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!).OperationTrace;

    private static async Task<Seed> SeedAsync(MigratedSqliteFixture fixture)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "authored exit", Requirements = "requirements", Notes = "notes", InGameId = "game-exit", InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "authored check", Requirements = "requirements", Notes = "notes", InGameId = "game-check", InGamePositionZ = 9, LocalPositionX = 10, LocalPositionY = 11, LocalPositionZ = 12, EnableAnnotation = false };
        await using var db = fixture.CreateDbContext();
        db.AddRange(room, transition, check);
        await db.SaveChangesAsync();
        return new(room, transition, check);
    }

    private static async Task<ShowAndRemoveSeed> SeedShowAndRemoveAsync(MigratedSqliteFixture fixture)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var transitionShow = new RoomTransition { RoomId = room.Id, Alias = "s", FriendlyName = "show exit", Requirements = "r", InGamePositionX = 2, InGamePositionY = 3, AnnotationSceneUnitX = 2, AnnotationSceneUnitY = 3, EnableAnnotation = false };
        var transitionRemove = new RoomTransition { RoomId = room.Id, Alias = "r", FriendlyName = "remove exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 4, AnnotationSceneUnitY = 5 };
        var checkShow = new CheckLocation { RoomId = room.Id, FriendlyName = "show check", Requirements = "r", InGamePositionX = 6, InGamePositionY = 7, AnnotationSceneUnitX = 6, AnnotationSceneUnitY = 7, EnableAnnotation = false };
        var checkRemove = new CheckLocation { RoomId = room.Id, FriendlyName = "remove check", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 8, AnnotationSceneUnitY = 9 };
        await using var db = fixture.CreateDbContext(); db.AddRange(room, transitionShow, transitionRemove, checkShow, checkRemove); await db.SaveChangesAsync();
        return new(room, transitionShow, transitionRemove, checkShow, checkRemove);
    }

    private static async Task<ConnectionSeed> SeedConnectionAsync(MigratedSqliteFixture fixture, bool bidirectional, bool unresolved = false, bool equalSortOrderDeterministicIds = false)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var first = new SubroomConnection { Id = equalSortOrderDeterministicIds ? Guid.Parse("00000000-0000-0000-0000-000000000001") : Guid.NewGuid(), RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = unresolved ? "missing-a" : "a", DestinationSubroomReferenceText = unresolved ? "missing-b" : "b", Requirements = "r", EnableAnnotation = false };
        var second = bidirectional ? new SubroomConnection { Id = equalSortOrderDeterministicIds ? Guid.Parse("00000000-0000-0000-0000-000000000002") : Guid.NewGuid(), RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", Requirements = "r", SortOrder = equalSortOrderDeterministicIds ? 0 : 1, EnableAnnotation = false } : null;
        await using var db = fixture.CreateDbContext(); db.AddRange(room, a, b, first); if (second is not null) db.Add(second); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync();
        return new(room, first, second);
    }

    private static async Task<IneligibleConnectionSeed> SeedIneligibleConnectionAsync(MigratedSqliteFixture fixture, string state)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var row = new SubroomConnection { RoomId = room.Id, Alias = state == "blank-alias" ? "" : state == "invalid-alias" ? "long" : "c", FriendlyName = state == "missing-name" ? "" : "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = state == "already-enabled", SceneUnitX = state == "already-enabled" ? 1 : null, SceneUnitY = state == "already-enabled" ? 2 : null, IsArchived = state == "archived" };
        var rows = new List<SubroomConnection> { row };
        if (state == "malformed") rows.Add(new() { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", SortOrder = 1 });
        await using var db = fixture.CreateDbContext();
        db.Add(room); if (state != "no-active-subroom") db.AddRange(a, b); db.AddRange(rows); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync();
        return new(room, rows);
    }

    private static async Task<Room> SeedEnabledRemoveIneligibleConnectionAsync(MigratedSqliteFixture fixture, string state)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 40, SceneUnitHeight = 40 };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var rows = state switch
        {
            "malformed" => new[]
            {
                new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = true, SceneUnitX = 12.5, SceneUnitY = 25.5 },
                new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", SortOrder = 1, EnableAnnotation = true, SceneUnitX = 12.5, SceneUnitY = 25.5 }
            },
            "archived" => new[]
            {
                new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = true, SceneUnitX = 12.5, SceneUnitY = 25.5, IsArchived = true, ArchivedUtc = DateTime.UtcNow }
            },
            "no-active-subroom" => new[]
            {
                new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "", DestinationSubroomReferenceText = "", Requirements = "r", EnableAnnotation = true, SceneUnitX = 12.5, SceneUnitY = 25.5 }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

        await using var db = fixture.CreateDbContext();
        db.Add(room);
        if (state != "no-active-subroom") db.AddRange(a, b);
        db.AddRange(rows);
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        return room;
    }

    private static async Task<ConnectionPersistence[]> ConnectionPersistenceAsync(MigratedSqliteFixture fixture, Guid roomId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SubroomConnections.Where(row => row.RoomId == roomId).OrderBy(row => row.Id)
            .Select(row => new ConnectionPersistence(row.Id, row.EnableAnnotation, row.SceneUnitX, row.SceneUnitY, row.IsArchived, row.UpdatedUtc))
            .ToArrayAsync();
    }

    private sealed record Seed(Room Room, RoomTransition Transition, CheckLocation Check);
    private sealed record ShowAndRemoveSeed(Room Room, RoomTransition TransitionShow, RoomTransition TransitionRemove, CheckLocation CheckShow, CheckLocation CheckRemove);
    private sealed record ConnectionSeed(Room Room, SubroomConnection First, SubroomConnection? Second);
    private sealed record IneligibleConnectionSeed(Room Room, IReadOnlyList<SubroomConnection> Rows);
    private sealed record ConnectionPersistence(Guid Id, bool EnableAnnotation, double? SceneUnitX, double? SceneUnitY, bool IsArchived, DateTime UpdatedUtc);
    private sealed class CountingLoader(IRoomEditorV2LogicLoader inner, ISceneLayoutLoader scene) : IRoomEditorV2LogicLoader, IRoomEditorV2SceneBatchLoader, IRoomEditorV2SceneSource
    {
        public int Loads { get; private set; }
        ISceneLayoutLoader? IRoomEditorV2SceneSource.SceneLoader => scene;
        public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
        {
            Loads++;
            return await inner.LoadAsync(roomId, cancellationToken);
        }
        public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, bool includeScene, CancellationToken cancellationToken)
        {
            Loads++;
            return inner is IRoomEditorV2SceneBatchLoader batch
                ? await batch.LoadAsync(roomId, includeScene, cancellationToken)
                : await inner.LoadAsync(roomId, cancellationToken);
        }
    }

}
