using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class TransitionFieldDiffPersistenceTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-transition-diff-{Guid.NewGuid():N}.db");
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task RealSqlite_DisjointExternalFieldMergesAndDraftMetadataCannotOverwriteProtectedFields()
    {
        var id = await SeedAsync();
        var catalog = new LogicCatalogService(new TestDbContextFactory(path));
        await using var read = Context();
        var baseline = await read.RoomTransitions.AsNoTracking().SingleAsync(x => x.Id == id);
        var external = TransitionFieldDiffMapper.Clone(baseline);
        external.FriendlyName = "external name";
        await catalog.SaveWithOutcomeAsync(external);
        var draft = TransitionFieldDiffMapper.Clone(baseline);
        draft.Notes = "local note";
        draft.SortOrder = 999;
        draft.ResolvedDestinationRoomId = Guid.NewGuid();
        var patch = await catalog.SaveTransitionWithPatchAsync(baseline, draft, baseline.RoomId);

        Assert.Equal(ChildRowSaveStatus.Committed, patch.Status);
        await using var verify = Context();
        var durable = await verify.RoomTransitions.SingleAsync(x => x.Id == id);
        Assert.Equal("external name", durable.FriendlyName);
        Assert.Equal("local note", durable.Notes);
        Assert.Equal(0, durable.SortOrder);
        Assert.Null(durable.ResolvedDestinationRoomId);
    }

    [Fact]
    public async Task RealSqlite_OverlappingExternalFieldReturnsTypedConflictWithoutOverwrite()
    {
        var id = await SeedAsync();
        var catalog = new LogicCatalogService(new TestDbContextFactory(path));
        await using var read = Context();
        var baseline = await read.RoomTransitions.AsNoTracking().SingleAsync(x => x.Id == id);
        var external = TransitionFieldDiffMapper.Clone(baseline);
        external.Notes = "external note";
        await catalog.SaveWithOutcomeAsync(external);
        var draft = TransitionFieldDiffMapper.Clone(baseline);
        draft.Notes = "local note";
        var patch = await catalog.SaveTransitionWithPatchAsync(baseline, draft, baseline.RoomId);

        Assert.Equal(ChildRowSaveStatus.Conflict, patch.Status);
        await using var verify = Context();
        Assert.Equal("external note", (await verify.RoomTransitions.SingleAsync(x => x.Id == id)).Notes);
    }

    [Fact]
    public async Task RealSqlite_CoordinatorConflictRebasesDurableRowAndNextMeaningfulEditRetriesRetainedDraft()
    {
        var id = await SeedAsync();
        var catalog = new LogicCatalogService(new TestDbContextFactory(path));
        await using var read = Context();
        var baseline = await read.RoomTransitions.AsNoTracking().SingleAsync(x => x.Id == id);
        var external = TransitionFieldDiffMapper.Clone(baseline);
        external.Notes = "external note";
        external.FriendlyName = "external name";
        await catalog.SaveWithOutcomeAsync(external);

        var live = TransitionFieldDiffMapper.Clone(baseline);
        live.Notes = "local note";
        var coordinator = new ChildRowSaveCoordinator<RoomTransition>(baseline, new TransitionChildFieldDiffMapper());
        ChildRowSavePatch<RoomTransition>? conflict = null;
        RoomTransition? rebased = null;
        var saveCalls = 0;
        Task<ChildRowSavePatch<RoomTransition>> Save(RoomTransition old, RoomTransition draft)
        {
            saveCalls++;
            return catalog.SaveTransitionWithPatchAsync(old, draft, baseline.RoomId);
        }

        await coordinator.SaveAsync(live, Save, (_, _, _) => { }, (draft, patch) => { rebased = draft; conflict = patch; }, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));

        Assert.Equal(ChildRowSaveStatus.Conflict, conflict!.Status);
        Assert.NotNull(conflict.SavedRow);
        Assert.Equal("external name", rebased!.FriendlyName);
        Assert.Equal("local note", rebased.Notes);

        // The component boundary retains the rebased draft, so a blur without a
        // new edit does not turn an expected conflict into an implicit retry.
        live = rebased;
        await coordinator.SaveAsync(live, Save, (_, _, _) => { }, (_, _) => { }, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));
        Assert.Equal(1, saveCalls);

        live.Requirements = "new meaningful edit";
        var committed = false;
        await coordinator.SaveAsync(live, Save, (_, patch, _) => { committed = patch.Status == ChildRowSaveStatus.Committed; }, (_, _) => { }, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));

        Assert.True(committed);
        await using var verify = Context();
        var durable = await verify.RoomTransitions.SingleAsync(x => x.Id == id);
        Assert.Equal("external name", durable.FriendlyName);
        Assert.Equal("local note", durable.Notes);
        Assert.Equal("new meaningful edit", durable.Requirements);
    }

    [Fact]
    public async Task RealSqlite_MissingTransitionIsReloadRequiredAndCoordinatorDoesNotRetry()
    {
        var id = await SeedAsync();
        var catalog = new LogicCatalogService(new TestDbContextFactory(path));
        await using var read = Context();
        var baseline = await read.RoomTransitions.AsNoTracking().SingleAsync(x => x.Id == id);
        await using (var delete = Context())
        {
            delete.RoomTransitions.Remove(await delete.RoomTransitions.SingleAsync(x => x.Id == id));
            await delete.SaveChangesAsync();
        }

        var coordinator = new ChildRowSaveCoordinator<RoomTransition>(baseline, new TransitionChildFieldDiffMapper());
        var draft = TransitionFieldDiffMapper.Clone(baseline);
        draft.Notes = "retained locally but noneditable";
        var calls = 0;
        ChildRowSavePatch<RoomTransition>? missing = null;
        Task<ChildRowSavePatch<RoomTransition>> Save(RoomTransition old, RoomTransition current)
        {
            calls++;
            return catalog.SaveTransitionWithPatchAsync(old, current, baseline.RoomId);
        }

        await coordinator.SaveAsync(draft, Save, (_, _, _) => { }, (_, patch) => missing = patch, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));
        await coordinator.SaveAsync(draft, Save, (_, _, _) => { }, (_, _) => { }, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));

        Assert.Equal(ChildRowSaveStatus.Missing, missing!.Status);
        Assert.True(coordinator.IsReloadRequired);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Mapper_ReconciliationRetainsLaterLocalTypingWhileApplyingCommittedFieldsAndResolverState()
    {
        var baseline = new RoomTransition { Notes = "old", FriendlyName = "old name" };
        var request = TransitionFieldDiffMapper.Clone(baseline); request.Notes = "saved";
        var live = TransitionFieldDiffMapper.Clone(request); live.FriendlyName = "later typing";
        var committed = TransitionFieldDiffMapper.Clone(request); committed.UpdatedUtc = DateTime.UtcNow; committed.ResolvedDestinationRoomId = Guid.NewGuid();
        TransitionFieldDiffMapper.Reconcile(live, committed, request, [nameof(RoomTransition.Notes)], true);
        Assert.Equal("later typing", live.FriendlyName);
        Assert.Equal("saved", live.Notes);
        Assert.Equal(committed.ResolvedDestinationRoomId, live.ResolvedDestinationRoomId);
    }

    [Fact]
    public async Task SharedCoordinator_QueuesLaterTransitionEditAndUsesFieldLocalPatches()
    {
        var baseline = new RoomTransition { Id = Guid.NewGuid(), FriendlyName = "before", Notes = "before", UpdatedUtc = DateTime.UnixEpoch };
        var live = TransitionFieldDiffMapper.Clone(baseline);
        var coordinator = new ChildRowSaveCoordinator<RoomTransition>(baseline, new TransitionChildFieldDiffMapper());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<RoomTransition>();

        async Task<ChildRowSavePatch<RoomTransition>> Save(RoomTransition old, RoomTransition draft)
        {
            calls.Add(TransitionFieldDiffMapper.Clone(draft));
            if (calls.Count == 1) { entered.SetResult(); await release.Task; }
            var committed = TransitionFieldDiffMapper.Clone(draft);
            committed.UpdatedUtc = old.UpdatedUtc.AddSeconds(calls.Count);
            return new(ChildRowSaveStatus.Committed, committed, TransitionFieldDiffMapper.Differences(old, draft), [committed], [], CatalogSaveOutcome.Unchanged);
        }

        live.FriendlyName = "first";
        var first = coordinator.SaveAsync(live, Save, (request, patch, _) => TransitionFieldDiffMapper.Reconcile(live, patch.SavedRow!, request, patch.CommittedFields, false), (_, _) => { }, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));
        await entered.Task;
        Assert.True(coordinator.IsSaving);
        live.Notes = "queued";
        var second = coordinator.SaveAsync(live, Save, (request, patch, _) => TransitionFieldDiffMapper.Reconcile(live, patch.SavedRow!, request, patch.CommittedFields, false), (_, _) => { }, _ => throw new Xunit.Sdk.XunitException("Unexpected failure."));
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, calls.Count);
        Assert.Equal("first", calls[1].FriendlyName);
        Assert.Equal("queued", calls[1].Notes);
        Assert.False(coordinator.IsSaving);
    }

    private async Task<Guid> SeedAsync()
    {
        await using var db = Context();
        var room = new Room { Id = Guid.NewGuid(), ReferenceId = "room", FriendlyName = "Room", SortOrder = 0 };
        var transition = new RoomTransition { Id = Guid.NewGuid(), RoomId = room.Id, Alias = "A", FriendlyName = "Exit", Requirements = "", Notes = "old", SortOrder = 0 };
        db.AddRange(room, transition); await db.SaveChangesAsync(); return transition.Id;
    }
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private sealed class TestDbContextFactory(string databasePath) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
    }
}
