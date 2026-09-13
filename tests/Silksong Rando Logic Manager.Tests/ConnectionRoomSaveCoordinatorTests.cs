using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class ConnectionRoomSaveCoordinatorTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"connection-coordinator-{Guid.NewGuid():N}.db");
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task GatedAnnotationThenStaleSiblingInlineSave_PreservesGeometryAndAuthoredControls()
    {
        var (room, first, second) = await SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new ConnectionRoomSaveCoordinator(Catalog(), async () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; }
        });
        coordinator.RegisterRows([first, second]);
        var move = coordinator.MoveAnnotationAsync(first.Id, room.Id, 12, 13);
        await entered.Task;
        var stale = Copy(second); stale.Requirements = "new logic"; stale.IsTodo = true; stale.IsVerified = true;
        var save = coordinator.SaveAsync(stale, room.Id);
        release.SetResult();
        await Task.WhenAll(move, save);

        await using var db = Context();
        var rows = await db.SubroomConnections.OrderBy(row => row.SortOrder).ToListAsync();
        Assert.All(rows, row => { Assert.True(row.EnableAnnotation); Assert.Equal(12, row.SceneUnitX); Assert.Equal(13, row.SceneUnitY); });
        var durable = Assert.Single(rows, row => row.Id == second.Id);
        Assert.Equal("new logic", durable.Requirements); Assert.True(durable.IsTodo); Assert.True(durable.IsVerified);
    }

    [Fact]
    public async Task GatedAnnotationThenStaleAliasSave_ReconcilesSplitFromCurrentGeometry()
    {
        var (room, first, second) = await SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new ConnectionRoomSaveCoordinator(Catalog(), async () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; }
        });
        coordinator.RegisterRows([first, second]);
        var move = coordinator.MoveAnnotationAsync(first.Id, room.Id, 5, 6);
        await entered.Task;
        var stale = Copy(first); stale.Alias = "B";
        var save = coordinator.SaveAsync(stale, room.Id);
        release.SetResult();
        await Task.WhenAll(move, save);

        await using var db = Context();
        var split = await db.SubroomConnections.SingleAsync(row => row.Id == first.Id);
        var retained = await db.SubroomConnections.SingleAsync(row => row.Id == second.Id);
        Assert.Equal("B", split.Alias); Assert.False(split.EnableAnnotation); Assert.Null(split.SceneUnitX); Assert.Null(split.SceneUnitY);
        Assert.True(retained.EnableAnnotation); Assert.Equal(5, retained.SceneUnitX); Assert.Equal(6, retained.SceneUnitY);
    }

    [Fact]
    public async Task FailedSave_RetryingNewestSnapshotPersistsNewestValue()
    {
        var (room, first, _) = await SeedAsync();
        var attempts = 0;
        var coordinator = new ConnectionRoomSaveCoordinator(Catalog(), () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("forced save failure");
            return Task.CompletedTask;
        });
        coordinator.RegisterRows([first]);
        var failed = Copy(first); failed.Notes = "first local value";
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SaveAsync(failed, room.Id));
        var newest = Copy(first); newest.Notes = "newest retry value";
        await coordinator.SaveAsync(newest, room.Id);

        await using var db = Context();
        Assert.Equal("newest retry value", (await db.SubroomConnections.SingleAsync(row => row.Id == first.Id)).Notes);
    }

    [Fact]
    public async Task FieldDiffPatch_MergesDisjointExternalEdit_AndConflictsOnSameField()
    {
        var (room, first, _) = await SeedAsync();
        var mapper = new ConnectionFieldDiffMapper();
        var disjointBaseline = mapper.Clone(first);
        await using (var external = Context())
        {
            var row = await external.SubroomConnections.SingleAsync(item => item.Id == first.Id);
            row.FriendlyName = "external name";
            await external.SaveChangesAsync();
        }
        var draft = mapper.Clone(first); draft.Notes = "local notes";
        var merged = await Catalog().SaveConnectionWithPatchAsync(disjointBaseline, draft, room.Id);
        Assert.Equal(ChildRowSaveStatus.Committed, merged.Status);
        Assert.Equal("local notes", merged.SavedRow!.Notes);
        Assert.Equal("external name", merged.SavedRow.FriendlyName);

        var conflictBaseline = mapper.Clone(merged.SavedRow);
        await using (var external = Context())
        {
            var row = await external.SubroomConnections.SingleAsync(item => item.Id == first.Id);
            row.Notes = "external notes";
            await external.SaveChangesAsync();
        }
        var conflictingDraft = mapper.Clone(conflictBaseline); conflictingDraft.Notes = "local conflict";
        var conflict = await Catalog().SaveConnectionWithPatchAsync(conflictBaseline, conflictingDraft, room.Id);
        Assert.Equal(ChildRowSaveStatus.Conflict, conflict.Status);
        Assert.Equal("external notes", conflict.SavedRow!.Notes);
    }

    [Fact]
    public async Task FieldDiffPatch_MissingRowIsReloadRequiredOutcome()
    {
        var (room, first, _) = await SeedAsync();
        await using (var db = Context()) { db.Remove(await db.SubroomConnections.SingleAsync(item => item.Id == first.Id)); await db.SaveChangesAsync(); }
        var patch = await Catalog().SaveConnectionWithPatchAsync(new ConnectionFieldDiffMapper().Clone(first), Copy(first), room.Id);
        Assert.Equal(ChildRowSaveStatus.Missing, patch.Status);
        Assert.Null(patch.SavedRow);
    }

    private async Task<(Room Room, SubroomConnection First, SubroomConnection Second)> SeedAsync()
    {
        await using var db = Context();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
        db.Add(room); await db.SaveChangesAsync();
        var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var destination = new Subroom { RoomId = room.Id, FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
        var first = new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", SortOrder = 0, EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2 };
        var second = new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "destination", DestinationSubroomReferenceText = "source", SortOrder = 1, EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2 };
        db.AddRange(source, destination, first, second); await db.SaveChangesAsync(); return (room, first, second);
    }

    private LogicCatalogService Catalog() => new(new Factory(path));
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private static SubroomConnection Copy(SubroomConnection row) => new() { Id = row.Id, RoomId = row.RoomId, Alias = row.Alias, FriendlyName = row.FriendlyName, SourceSubroomReferenceText = row.SourceSubroomReferenceText, DestinationSubroomReferenceText = row.DestinationSubroomReferenceText, Requirements = row.Requirements, Notes = row.Notes, IsTodo = row.IsTodo, IsVerified = row.IsVerified, EnableAnnotation = row.EnableAnnotation, SceneUnitX = row.SceneUnitX, SceneUnitY = row.SceneUnitY, UpdatedUtc = row.UpdatedUtc };
    private sealed class Factory(string databasePath) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
}
