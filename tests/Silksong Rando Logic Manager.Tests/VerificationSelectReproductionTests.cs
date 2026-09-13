using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

/// <summary>
/// The closest automated reproduction permitted by the current test strategy:
/// real SQLite document projection and persisted-row save paths. Browser/circuit
/// behavior remains covered by the repeatable manual procedure in TESTING_STRATEGY.
/// </summary>
public sealed class VerificationSelectReproductionTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-verification-select-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task PersistedVerificationStatesSurviveDocumentProjectionInteractiveInitializationSelectionAndReload()
    {
        var roomId = await SeedAsync();
        var catalog = new LogicCatalogService(new Factory(path));
        var expected = await ReadSnapshotsAsync();

        // Initial server render projection.
        var initial = Assert.IsType<RoomDocument>(await catalog.GetRoomAsync(roomId));
        AssertDocumentValues(initial, expected);
        AssertSnapshotsEqual(expected, await ReadSnapshotsAsync());

        // Interactive initialization reuses a fresh document projection. It must
        // not reconcile any persisted verification state back to the database.
        var interactive = Assert.IsType<RoomDocument>(await catalog.GetRoomAsync(roomId));
        AssertDocumentValues(interactive, expected);
        AssertSnapshotsEqual(expected, await ReadSnapshotsAsync());

        foreach (var transition in interactive.Transitions)
        {
            var draft = TransitionFieldDiffMapper.Clone(transition);
            draft.IsVerified = Next(transition.IsVerified);
            if (transition.IsArchived)
            {
                Assert.Equal(ChildRowSaveStatus.Missing, (await catalog.SaveTransitionWithPatchAsync(transition, draft, roomId)).Status);
                continue;
            }
            Assert.Equal(ChildRowSaveStatus.Committed, (await catalog.SaveTransitionWithPatchAsync(transition, draft, roomId)).Status);
        }
        foreach (var connection in interactive.Connections)
        {
            var mapper = new ConnectionFieldDiffMapper();
            var draft = mapper.Clone(connection);
            draft.IsVerified = Next(connection.IsVerified);
            if (connection.IsArchived)
            {
                var beforeRejectedSave = expected[connection.Id];
                var outcome = await catalog.SaveConnectionWithPatchAsync(connection, draft, roomId);
                Assert.Equal(ChildRowSaveStatus.Missing, outcome.Status);
                Assert.Null(outcome.SavedRow);
                Assert.Equal(beforeRejectedSave, (await ReadSnapshotsAsync())[connection.Id]);
                continue;
            }
            Assert.Equal(ChildRowSaveStatus.Committed, (await catalog.SaveConnectionWithPatchAsync(connection, draft, roomId)).Status);
        }
        foreach (var check in interactive.Checks)
        {
            var mapper = new CheckFieldDiffMapper();
            var draft = mapper.Clone(check);
            draft.IsVerified = Next(check.IsVerified);
            Assert.Equal(ChildRowSaveStatus.Committed, (await catalog.SaveCheckWithPatchAsync(check, draft, roomId)).Status);
        }

        var afterSelection = await ReadSnapshotsAsync();
        Assert.Equal(expected.Keys.Order(), afterSelection.Keys.Order());
        foreach (var (id, before) in expected)
        {
            var saved = afterSelection[id];
            var archived = interactive.Transitions.Any(x => x.Id == id && x.IsArchived)
                || interactive.Connections.Any(x => x.Id == id && x.IsArchived);
            if (archived)
            {
                Assert.Equal(before, saved);
            }
            else
            {
                Assert.Equal(Next(before.Value), saved.Value);
                Assert.True(saved.UpdatedUtc > before.UpdatedUtc, $"Selected row {id} did not advance UpdatedUtc.");
            }
        }

        var reloaded = Assert.IsType<RoomDocument>(await catalog.GetRoomAsync(roomId));
        AssertDocumentValues(reloaded, afterSelection);
        AssertSnapshotsEqual(afterSelection, await ReadSnapshotsAsync());
    }

    private async Task<Guid> SeedAsync()
    {
        await using var db = Context();
        var room = new Room { FriendlyName = "Verification reproduction", ReferenceId = "verification-reproduction", SortOrder = 0 };
        var values = new bool?[] { true, false, null };
        for (var stateIndex = 0; stateIndex < values.Length; stateIndex++)
        {
            foreach (var archived in new[] { false, true })
            {
                var suffix = $"{stateIndex}-{(archived ? "archived" : "active")}";
                db.Add(new RoomTransition { Room = room, Alias = $"T{stateIndex}{(archived ? "A" : "L")}", FriendlyName = $"Transition {suffix}", SortOrder = stateIndex * 2 + (archived ? 1 : 0), IsArchived = archived, IsVerified = values[stateIndex] });
                db.Add(new SubroomConnection { Room = room, Alias = $"C{stateIndex}{(archived ? "A" : "L")}", FriendlyName = $"Connection {suffix}", SourceSubroomReferenceText = string.Empty, DestinationSubroomReferenceText = string.Empty, SortOrder = stateIndex * 2 + (archived ? 1 : 0), IsArchived = archived, IsVerified = values[stateIndex] });
                db.Add(new CheckLocation { Room = room, FriendlyName = $"Check {suffix}", SortOrder = stateIndex * 2 + (archived ? 1 : 0), IsArchived = archived, IsVerified = values[stateIndex] });
            }
        }
        await db.SaveChangesAsync();
        return room.Id;
    }

    private async Task<Dictionary<Guid, VerificationSnapshot>> ReadSnapshotsAsync()
    {
        await using var db = Context();
        var transitions = await db.RoomTransitions.AsNoTracking().Select(x => new VerificationSnapshot(x.Id, x.IsVerified, x.UpdatedUtc)).ToListAsync();
        var connections = await db.SubroomConnections.AsNoTracking().Select(x => new VerificationSnapshot(x.Id, x.IsVerified, x.UpdatedUtc)).ToListAsync();
        var checks = await db.CheckLocations.AsNoTracking().Select(x => new VerificationSnapshot(x.Id, x.IsVerified, x.UpdatedUtc)).ToListAsync();
        return transitions.Concat(connections).Concat(checks).ToDictionary(x => x.Id);
    }

    private static void AssertDocumentValues(RoomDocument document, IReadOnlyDictionary<Guid, VerificationSnapshot> expected)
    {
        foreach (var row in document.Transitions) Assert.Equal(expected[row.Id].Value, row.IsVerified);
        foreach (var row in document.Connections) Assert.Equal(expected[row.Id].Value, row.IsVerified);
        foreach (var row in document.Checks) Assert.Equal(expected[row.Id].Value, row.IsVerified);
    }

    private static void AssertSnapshotsEqual(IReadOnlyDictionary<Guid, VerificationSnapshot> expected, IReadOnlyDictionary<Guid, VerificationSnapshot> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (id, snapshot) in expected) Assert.Equal(snapshot, actual[id]);
    }

    private static bool? Next(bool? value) => value switch { true => false, false => null, _ => true };
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private sealed class Factory(string databasePath) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed record VerificationSnapshot(Guid Id, bool? Value, DateTime UpdatedUtc);
}
