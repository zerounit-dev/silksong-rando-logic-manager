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

/// <summary>Catalogue-scale direct-service trace for ordinary non-scene V2 header writes.</summary>
public sealed class RoomEditorV2HeaderServerTraceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-v2-header-trace-{Guid.NewGuid():N}.db");
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); var groups = Enumerable.Range(0, 4).Select(i => new RoomGroup { FriendlyName = $"G{i}", SortOrder = i }).ToArray(); db.AddRange(groups); var rooms = Enumerable.Range(0, 58).Select(i => new Room { RoomGroupId = groups[i % 4].Id, FriendlyName = $"Room {i}", ReferenceId = $"room-{i}", SortOrder = i }).ToArray(); db.AddRange(rooms); db.AddRange(Enumerable.Range(0, 78).Select(i => new Subroom { RoomId = rooms[i % 58].Id, FriendlyName = $"S{i}", ReferenceId = $"s{i}" })); db.AddRange(Enumerable.Range(0, 159).Select(i => new RoomTransition { RoomId = rooms[i % 58].Id, FriendlyName = $"T{i}", Alias = $"E{i}" })); db.AddRange(Enumerable.Range(0, 109).Select(i => new SubroomConnection { RoomId = rooms[i % 58].Id, FriendlyName = $"C{i}", Alias = $"C{i}" })); db.AddRange(Enumerable.Range(0, 139).Select(i => new CheckLocation { RoomId = rooms[i % 58].Id, FriendlyName = $"K{i}" })); await db.SaveChangesAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsV2OrdinaryHeaderSaveBreakdowns()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        await Samples("sequence-first-use", 1); await Samples("warmup", 1); await Samples("warm", 3);
        async Task Samples(string phase, int count)
        {
            for (var sample = 1; sample <= count; sample++)
                foreach (var field in new[] { "FriendlyName", "InGameId", "Contributors", "Comments" })
                {
                    await using var read = Context(); var room = await read.Rooms.AsNoTracking().OrderBy(x => x.SortOrder).FirstAsync(); var baseline = new RoomHeaderDurableBaseline(room.Id, room.UpdatedUtc, room.FriendlyName, room.InGameId, room.Contributors, room.Comments);
                    RoomHeaderDraft draft = field switch { "FriendlyName" => new($"Name {phase}-{sample}", room.InGameId, room.Contributors, room.Comments), "InGameId" => new(room.FriendlyName, $"game-{phase}-{sample}", room.Contributors, room.Comments), "Contributors" => new(room.FriendlyName, room.InGameId, $"contributors-{phase}-{sample}", room.Comments), _ => new(room.FriendlyName, room.InGameId, room.Contributors, $"comments-{phase}-{sample}") };
                    sql.Reset(); var clock = Stopwatch.StartNew(); var result = await commands.SaveRoomHeaderAsync(room.Id, baseline, draft); clock.Stop(); Assert.Equal(V2RoomHeaderCommandStatus.Committed, result.Status);
                    output.WriteLine($"V2 header {phase} trace | sample={sample} | {field} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
                    if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"{field} warm sample {sample} exceeded the 50ms direct-service budget.");
                }
        }
    }
    [Fact]
    public async Task RoomReferenceProposal_PreparationAndWarmCommittedOutcomes_RecordBreakdowns()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        await Samples("sequence-first-use", 1); await Samples("warmup", 1); await Samples("warm", 3);
        async Task Samples(string phase, int count)
        {
            for (var sample = 1; sample <= count; sample++) foreach (var update in new[] { true, false })
            {
                await using var read = Context(); var room = await read.Rooms.AsNoTracking().OrderBy(x => x.SortOrder).FirstAsync();
                var baseline = RoomEditorV2CommandService.HeaderFromView(new RoomHeaderView(room.Id, room.FriendlyName, room.ReferenceId, room.InGameId, room.Contributors, room.Comments, null, null, false, false, room.IsArchived, room.UpdatedUtc, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral));
                var draft = new RoomHeaderDraft(room.FriendlyName, room.InGameId, room.Contributors, room.Comments, $"room-reference-{phase}-{sample}-{update}");
                sql.Reset(); var preparation = Stopwatch.StartNew(); var prepared = await commands.PrepareRoomReferenceRenameAsync(room.Id, baseline, draft); preparation.Stop(); Assert.Equal(V2RoomHeaderCommandStatus.Proposal, prepared.Status);
                output.WriteLine($"V2 room-reference preparation | {phase} | update={update} | service={preparation.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | map=0 | scene=0");
                sql.Reset(); var clock = Stopwatch.StartNew(); var result = await commands.ApplyRoomReferenceRenameAsync(room.Id, prepared.Proposal!, update); clock.Stop(); Assert.Equal(V2RoomHeaderCommandStatus.Committed, result.Status);
                output.WriteLine($"V2 room-reference {phase} trace | sample={sample} | outcome={(update ? "update" : "do-not-update")} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
                if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"room-reference warm {update} sample {sample} exceeded the 50ms budget.");
            }
        }
    }
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private sealed class Factory(string database, SqlTrace trace) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext() => Create(); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create()); private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={database}").AddInterceptors(trace).Options); }
    private sealed class SqlTrace : DbCommandInterceptor { private readonly Dictionary<DbCommand, Stopwatch> timers = []; public int Count { get; private set; } public TimeSpan Elapsed { get; private set; } public void Reset() { timers.Clear(); Count = 0; Elapsed = TimeSpan.Zero; } public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken t = default) { Start(c); return ValueTask.FromResult(r); } public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader r, CancellationToken t = default) { Stop(c); return ValueTask.FromResult(r); } public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken t = default) { Start(c); return ValueTask.FromResult(r); } public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken t = default) { Stop(c); return ValueTask.FromResult(r); } private void Start(DbCommand c) { Count++; timers[c] = Stopwatch.StartNew(); } private void Stop(DbCommand c) { if (timers.Remove(c, out var timer)) Elapsed += timer.Elapsed; } }
}
