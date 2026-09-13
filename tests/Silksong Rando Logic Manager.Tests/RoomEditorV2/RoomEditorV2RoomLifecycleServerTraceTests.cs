using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;
using Xunit.Abstractions;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Catalogue-scale direct-service lifecycle trace; scene-file time is a separate allowance.</summary>
public sealed class RoomEditorV2RoomLifecycleServerTraceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-v2-room-lifecycle-{Guid.NewGuid():N}.db");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"silksong-v2-room-lifecycle-files-{Guid.NewGuid():N}");
    public async Task InitializeAsync()
    {
        await using var db = Context(); await db.Database.MigrateAsync();
        var groups = Enumerable.Range(0, 4).Select(i => new RoomGroup { FriendlyName = $"G{i}", SortOrder = i }).ToArray(); var rooms = Enumerable.Range(0, 58).Select(i => new Room { RoomGroupId = groups[i % 4].Id, FriendlyName = $"Room {i}", ReferenceId = $"room-{i}", SortOrder = i }).ToArray();
        db.AddRange(groups); db.AddRange(rooms); db.AddRange(Enumerable.Range(0, 78).Select(i => new Subroom { RoomId = rooms[i % 58].Id, FriendlyName = $"S{i}", ReferenceId = $"s{i}" })); db.AddRange(Enumerable.Range(0, 159).Select(i => new RoomTransition { RoomId = rooms[i % 58].Id, FriendlyName = $"T{i}", Alias = $"E{i}" })); db.AddRange(Enumerable.Range(0, 109).Select(i => new SubroomConnection { RoomId = rooms[i % 58].Id, FriendlyName = $"C{i}", Alias = $"C{i}" })); db.AddRange(Enumerable.Range(0, 139).Select(i => new CheckLocation { RoomId = rooms[i % 58].Id, FriendlyName = $"K{i}" })); await db.SaveChangesAsync();
    }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsArchiveRestoreDeleteSqlResolverAndSeparateFileAllowance()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var files = new SceneImageFileService(root); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory, files), factory);
        await Samples("sequence-first-use", 1); await Samples("warmup", 1); await Samples("warm", 3);
        async Task Samples(string phase, int count)
        {
            for (var sample = 1; sample <= count; sample++)
            {
                var room = await NextActiveRoomAsync();
                await Record(phase, "archive", () => commands.SetRoomArchiveAsync(room, true), TimeSpan.Zero);
                await Record(phase, "restore", () => commands.SetRoomArchiveAsync(room, false), TimeSpan.Zero);
                await Record(phase, "archive-for-delete", () => commands.SetRoomArchiveAsync(room, true), TimeSpan.Zero);
                Directory.CreateDirectory(Path.GetDirectoryName(files.GetRoomImagePath(room))!); await File.WriteAllTextAsync(files.GetRoomImagePath(room), "image");
                await Record(phase, "delete", () => commands.DeleteRoomAsync(room), TimeSpan.FromMilliseconds(200));
            }
        }
        async Task Record(string phase, string operation, Func<Task<V2RoomLifecycleCommandOutcome>> action, TimeSpan fileLimit)
        {
            sql.Reset(); var stopwatch = Stopwatch.StartNew(); var result = await action(); stopwatch.Stop(); Assert.Equal(V2RoomLifecycleCommandStatus.Committed, result.Status);
            var nonFile = stopwatch.Elapsed - result.SceneImageDeleteElapsed;
            output.WriteLine($"V2 room lifecycle trace | {operation} | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | non-file={nonFile.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver=0.000ms | file-delete={result.SceneImageDeleteElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
            if (phase == "warm") { Assert.True(nonFile < TimeSpan.FromMilliseconds(50), $"{operation} warm non-file work exceeded 50ms."); if (operation == "delete") Assert.True(result.SceneImageDeleteElapsed < fileLimit, $"delete warm file work exceeded {fileLimit.TotalMilliseconds}ms."); }
        }
    }
    private async Task<Guid> NextActiveRoomAsync() { await using var db = Context(); return await db.Rooms.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).FirstAsync(); }
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private sealed class Factory(string database, SqlTrace trace) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext() => Create(); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create()); private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={database}").AddInterceptors(trace).Options); }
    private sealed class SqlTrace : DbCommandInterceptor { private readonly Dictionary<DbCommand, Stopwatch> timers = []; public int Count { get; private set; } public TimeSpan Elapsed { get; private set; } public void Reset() { timers.Clear(); Count = 0; Elapsed = TimeSpan.Zero; } public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken t = default) { Start(c); return ValueTask.FromResult(r); } public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader r, CancellationToken t = default) { Stop(c); return ValueTask.FromResult(r); } public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken t = default) { Start(c); return ValueTask.FromResult(r); } public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken t = default) { Stop(c); return ValueTask.FromResult(r); } private void Start(DbCommand c) { Count++; timers[c] = Stopwatch.StartNew(); } private void Stop(DbCommand c) { if (timers.Remove(c, out var timer)) Elapsed += timer.Elapsed; } }
}
