using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using System.Data.Common;
using System.Reflection;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2HeaderReferenceRevertPageTests
{
    [Fact]
    public async Task RenderedHeaderReferenceRevert_WritesNothingRefreshesNothingRestoresBaselineAndFocusesInitiatorOnce()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (source, transition, scene) = await SeedAsync(fixture);
        var writes = new SqliteWriteTrace();
        var contexts = new InterceptingFactory(fixture.DatabasePath, writes);
        var loader = new CountingLoader(new RoomEditorV2LogicLoader(contexts));
        var commands = new ReferenceRevertCommandSpy(new RoomEditorV2CommandService(new LogicCatalogService(contexts), contexts));
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true);
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);

        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Equal("old", page.Find("textarea[aria-label='Room reference ID']").TextContent));
        Assert.Equal(1, loader.LoadCount);
        writes.Reset();

        var reference = page.Find("textarea[aria-label='Room reference ID']");
        reference.Input("renamed");
        await reference.TriggerEventAsync("onblur", new FocusEventArgs());
        page.WaitForAssertion(() => Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage")));
        Assert.Equal(1, commands.PrepareCalls);
        Assert.Equal(0, commands.ApplyCalls);
        var openModal = Assert.IsType<V2ModalRuntimeState>(typeof(RoomEditorV2Page).GetField("modalState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance));
        Assert.Null(openModal.PendingTargetId);

        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "revert change").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal("old", page.Find("textarea[aria-label='Room reference ID']").TextContent);
            Assert.Equal(1, commands.RevertCalls);
            Assert.Equal(0, commands.ApplyCalls);
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(0, writes.MutationCount);
            Assert.Equal(1, FocusCallCount(context, "room-reference-id"));
            Assert.Null(typeof(RoomEditorV2Page).GetField("pendingModalFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance));
        });

        await using var verify = fixture.CreateDbContext();
        var durableSource = await verify.Rooms.SingleAsync(x => x.Id == source.Id);
        var durableTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
        var durableScene = await verify.MapScenes.SingleAsync(x => x.Id == scene.Id);
        Assert.Equal(("old", source.UpdatedUtc), (durableSource.ReferenceId, durableSource.UpdatedUtc));
        Assert.Equal(("old", transition.UpdatedUtc), (durableTransition.DestinationRoomReferenceText, durableTransition.UpdatedUtc));
        Assert.Equal("old", durableScene.RoomReferenceText);
    }

    private static int FocusCallCount(TestContext context, string target) => context.JSInterop.Invocations.Count(x =>
        x.Identifier == "focusEditorField" && string.Equals((string?)x.Arguments[0], target, StringComparison.Ordinal));

    private static async Task<(Room Source, RoomTransition Transition, MapScene Scene)> SeedAsync(MigratedSqliteFixture fixture)
    {
        var source = new Room { FriendlyName = "Source", ReferenceId = "old", SortOrder = 0 };
        var target = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var transition = new RoomTransition { RoomId = target.Id, FriendlyName = "Target exit", Alias = "a", DestinationRoomReferenceText = "old", ResolvedDestinationRoomId = source.Id };
        await using var db = fixture.CreateDbContext();
        db.AddRange(source, target, transition);
        var map = new Map { InGameId = "map" }; db.Add(map); await db.SaveChangesAsync();
        var zone = new MapZone { MapId = map.Id, InGameId = "zone" }; db.Add(zone); await db.SaveChangesAsync();
        var scene = new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = "old", ResolvedRoomId = source.Id };
        db.Add(scene); await db.SaveChangesAsync();
        return (source, transition, scene);
    }

    private sealed class CountingLoader(IRoomEditorV2LogicLoader inner) : IRoomEditorV2LogicLoader
    {
        public int LoadCount { get; private set; }
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { LoadCount++; return inner.LoadAsync(roomId, cancellationToken); }
    }

    private sealed class ReferenceRevertCommandSpy(RoomEditorV2CommandService inner) : IRoomEditorV2CommandService
    {
        public int PrepareCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public Task<V2RoomHeaderCommandOutcome> PrepareRoomReferenceRenameAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft) { PrepareCalls++; return inner.PrepareRoomReferenceRenameAsync(roomId, baseline, draft); }
        public Task<V2RoomHeaderCommandOutcome> ApplyRoomReferenceRenameAsync(Guid roomId, RoomReferenceRenameProposal proposal, bool updateReferences) { ApplyCalls++; return inner.ApplyRoomReferenceRenameAsync(roomId, proposal, updateReferences); }
        public V2RoomHeaderCommandOutcome RevertRoomReferenceRename(RoomReferenceRenameProposal proposal) { RevertCalls++; return inner.RevertRoomReferenceRename(proposal); }
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    }

    private sealed class InterceptingFactory(string databasePath, DbCommandInterceptor interceptor) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").AddInterceptors(interceptor).Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class SqliteWriteTrace : DbCommandInterceptor
    {
        public int MutationCount { get; private set; }
        public void Reset() => MutationCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            RecordMutation(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            RecordMutation(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            RecordMutation(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RecordMutation(command);
            return ValueTask.FromResult(result);
        }

        private void RecordMutation(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) MutationCount++;
        }
    }
}
