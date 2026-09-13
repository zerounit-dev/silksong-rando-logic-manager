using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Rendered status-action routes backed by a migration-current SQLite database.</summary>
public sealed class RoomEditorV2TransitionInverseStatusPageTests
{
    [Fact]
    public async Task InverseIcon_EligibleDirtyDraft_OpensThenAppliesTargetOnlyAndRefreshes()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.SourceRoom.Id));
        var iconId = $"v2-transition-inverse-{seed.Source.Id}";

        page.Find($"[data-v2-transition-row='{seed.Source.Id}'] [data-v2-transition-field='notes']").Input("dirty draft");
        page.Find($"#{iconId}").Click();
        page.WaitForAssertion(() => Assert.Equal("set up inverse?", page.Find("#v2-page-modal-title").TextContent));
        AssertDialogFocus(context);
        Assert.Equal(1, loader.LoadCount);
        await AssertPersistedAsync(fixture, seed, "persisted", null, null);
        var sourceUpdatedUtc = (await ReadTransitionAsync(fixture, seed.Source.Id)).UpdatedUtc;

        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == "update inverse").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(2, loader.LoadCount);
            Assert.Contains("refresh #2", page.Markup);
        });
        await AssertPersistedAsync(fixture, seed, "persisted", "source", "out");
        Assert.Equal(sourceUpdatedUtc, (await ReadTransitionAsync(fixture, seed.Source.Id)).UpdatedUtc);
        AssertExactlyOneIconFocus(context, iconId);
    }

    [Theory]
    [InlineData("do not update inverse")]
    [InlineData("revert change")]
    public async Task InverseIcon_DoNotUpdateOrRevert_WritesNothingAndRestoresFocus(string outcome)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.SourceRoom.Id));
        var iconId = $"v2-transition-inverse-{seed.Source.Id}";

        page.Find($"[data-v2-transition-row='{seed.Source.Id}'] [data-v2-transition-field='notes']").Input("dirty draft");
        page.Find($"#{iconId}").Click();
        page.WaitForAssertion(() =>
        {
            var dialog = page.Find("#v2-page-modal-dialog");
            Assert.Equal("ProposalOpen", dialog.GetAttribute("data-v2-modal-stage"));
            Assert.Equal("set up inverse?", page.Find("#v2-page-modal-title").TextContent);
        });
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == outcome).Click();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            if (outcome == "do not update inverse") Assert.Contains("refresh #2", page.Markup);
        });

        Assert.Equal(outcome == "do not update inverse" ? 2 : 1, loader.LoadCount);
        await AssertPersistedAsync(fixture, seed, "persisted", null, null);
        AssertExactlyOneIconFocus(context, iconId);
    }

    [Fact]
    public async Task InverseIcon_NoLongerEligibleAtPreparation_OpensNoModalWritesNothingAndDoesNotRefresh()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.SourceRoom.Id));

        await using (var external = fixture.CreateDbContext())
        {
            var target = await external.RoomTransitions.SingleAsync(x => x.Id == seed.Target.Id);
            target.DestinationRoomReferenceText = "external";
            target.DestinationTransitionAliasText = "external-alias";
            await external.SaveChangesAsync();
        }
        page.Find($"#v2-transition-inverse-{seed.Source.Id}").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));

        Assert.Equal(1, loader.LoadCount);
        await AssertPersistedAsync(fixture, seed, "persisted", "external", "external-alias");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InverseIcon_SourceReferenceOrAliasBecomesAmbiguousBeforePreparation_OpensNoModalAndWritesNothing(bool aliasAmbiguity)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.SourceRoom.Id));
        var sourceUpdatedUtc = (await ReadTransitionAsync(fixture, seed.Source.Id)).UpdatedUtc;

        await using (var external = fixture.CreateDbContext())
        {
            if (aliasAmbiguity)
                external.RoomTransitions.Add(new RoomTransition { RoomId = seed.SourceRoom.Id, Alias = "out", FriendlyName = "Duplicate", Requirements = "r", SortOrder = 1 });
            else
                external.Rooms.Add(new Room { FriendlyName = "Duplicate", ReferenceId = "source", SortOrder = 2 });
            await external.SaveChangesAsync();
        }

        page.Find($"#v2-transition-inverse-{seed.Source.Id}").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));

        Assert.Equal(1, loader.LoadCount);
        await AssertPersistedAsync(fixture, seed, "persisted", null, null);
        Assert.Equal(sourceUpdatedUtc, (await ReadTransitionAsync(fixture, seed.Source.Id)).UpdatedUtc);
    }

    [Fact]
    public async Task InverseIcon_OneResolvedInverse_NavigatesToItsRoomWithoutWriting()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        await using (var setup = fixture.CreateDbContext())
        {
            var target = await setup.RoomTransitions.SingleAsync(x => x.Id == seed.Target.Id);
            target.DestinationRoomReferenceText = "source";
            target.DestinationTransitionAliasText = "out";
            await setup.SaveChangesAsync();
        }
        var sourceBefore = await ReadTransitionAsync(fixture, seed.Source.Id);
        var targetBefore = await ReadTransitionAsync(fixture, seed.Target.Id);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.SourceRoom.Id));
        var icon = page.Find($"#v2-transition-inverse-{seed.Source.Id}");

        Assert.Equal("One inverse transition found; open inverse room", icon.GetAttribute("title"));
        Assert.Contains("fa-circle-check", icon.InnerHtml);
        icon.Click();

        Assert.EndsWith($"/rooms/{seed.Target.RoomId:D}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(sourceBefore.UpdatedUtc, (await ReadTransitionAsync(fixture, seed.Source.Id)).UpdatedUtc);
        Assert.Equal(targetBefore.UpdatedUtc, (await ReadTransitionAsync(fixture, seed.Target.Id)).UpdatedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InverseIcon_ConcurrentOrMissingTarget_RefreshesCorrectionWithoutSourceWriteAndReturnsInitiatorFocus(bool missing)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, out var loader);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.SourceRoom.Id));
        var iconId = $"v2-transition-inverse-{seed.Source.Id}";

        page.Find($"#{iconId}").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        await using (var external = fixture.CreateDbContext())
        {
            var target = await external.RoomTransitions.SingleAsync(x => x.Id == seed.Target.Id);
            if (missing) target.IsArchived = true;
            else target.DestinationRoomReferenceText = "external";
            await external.SaveChangesAsync();
        }
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent!.Trim() == "update inverse").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(2, loader.LoadCount);
            Assert.Contains("refresh #2", page.Markup);
        });

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("persisted", (await verify.RoomTransitions.SingleAsync(x => x.Id == seed.Source.Id)).Notes);
        if (missing) Assert.True((await verify.RoomTransitions.SingleAsync(x => x.Id == seed.Target.Id)).IsArchived);
        else Assert.Equal("external", (await verify.RoomTransitions.SingleAsync(x => x.Id == seed.Target.Id)).DestinationRoomReferenceText);
        AssertExactlyOneIconFocus(context, iconId);
    }

    private static async Task AssertPersistedAsync(MigratedSqliteFixture fixture, Seed seed, string sourceNotes, string? targetRoom, string? targetAlias)
    {
        await using var verify = fixture.CreateDbContext();
        var source = await verify.RoomTransitions.SingleAsync(x => x.Id == seed.Source.Id);
        var target = await verify.RoomTransitions.SingleAsync(x => x.Id == seed.Target.Id);
        Assert.Equal(sourceNotes, source.Notes);
        Assert.Equal((targetRoom, targetAlias), (target.DestinationRoomReferenceText, target.DestinationTransitionAliasText));
    }

    private static async Task<RoomTransition> ReadTransitionAsync(MigratedSqliteFixture fixture, Guid id)
    {
        await using var verify = fixture.CreateDbContext();
        return await verify.RoomTransitions.SingleAsync(x => x.Id == id);
    }

    private static void AssertDialogFocus(TestContext context) =>
        Assert.Single(context.JSInterop.Invocations, x => x.Identifier == "focusV2ModalDialog");

    private static void AssertExactlyOneIconFocus(TestContext context, string id) =>
        Assert.Single(context.JSInterop.Invocations, x =>
            x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == id);

    private static TestContext PageContext(MigratedSqliteFixture fixture, out CountingLoader loader)
    {
        var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true);
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        loader = new(new RoomEditorV2LogicLoader(fixture));
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        return context;
    }

    private static async Task<Seed> SeedAsync(MigratedSqliteFixture fixture)
    {
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "in", Requirements = "r", Notes = "persisted", SortOrder = 0, ResolvedDestinationRoomId = targetRoom.Id };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = sourceRoom.Id };
        await using var db = fixture.CreateDbContext();
        db.AddRange(sourceRoom, targetRoom, source, target);
        await db.SaveChangesAsync();
        source.ResolvedDestinationTransitionId = target.Id;
        target.ResolvedDestinationTransitionId = source.Id;
        await db.SaveChangesAsync();
        return new(sourceRoom, source, target);
    }

    private sealed record Seed(Room SourceRoom, RoomTransition Source, RoomTransition Target);
    private sealed class CountingLoader(IRoomEditorV2LogicLoader inner) : IRoomEditorV2LogicLoader
    {
        public int LoadCount { get; private set; }
        public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken token)
        {
            LoadCount++;
            return await inner.LoadAsync(roomId, token);
        }
    }
}
