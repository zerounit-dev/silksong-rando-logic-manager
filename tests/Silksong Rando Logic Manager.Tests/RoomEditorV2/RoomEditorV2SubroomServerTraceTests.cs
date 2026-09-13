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

/// <summary>Catalogue-scale, direct V2 adapter traces. Rendering is intentionally outside each stopwatch.</summary>
public sealed class RoomEditorV2SubroomServerTraceTests : IAsyncLifetime
{
    private const int WarmSamples = 3;
    private readonly ITestOutputHelper output;
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-v2-subroom-trace-{Guid.NewGuid():N}.db");
    public RoomEditorV2SubroomServerTraceTests(ITestOutputHelper output) => this.output = output;
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); await SeedAsync(db); await new LogicReferenceResolver(db).ResolveAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsV2SubroomTextCreationOrderingAndLifecycleBreakdowns()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        // One serial diagnostic sequence: only its first operation is cold.
        await Samples("sequence-first-use", 1, commands, sql);
        // Exercise every adapter/query shape outside the compliance interval.
        await Samples("warmup", 1, commands, sql);
        await Samples("warm", WarmSamples, commands, sql);
    }

    [Fact]
    public async Task ReferenceProposalWarmTrace_RecordsUpdateAndDoNotUpdateWithoutMapOrScene()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        foreach (var phase in new[] { "sequence-first-use", "warmup", "warm" }) for (var sample = 1; sample <= (phase == "warm" ? WarmSamples : 1); sample++) foreach (var update in new[] { true, false })
        {
            var room = await RoomId(); var source = await ActiveSubroom(room); var baseline = Baseline(source); var draft = new SubroomDraft(Guid.NewGuid(), source.FriendlyName, $"rename-{phase}-{sample}-{update}", source.Notes ?? "", source.SceneUnitX, source.SceneUnitY, source.SceneUnitWidth, source.SceneUnitHeight);
            var proposal = await commands.PrepareSubroomReferenceRenameAsync(room, baseline, draft); Assert.Equal(V2SubroomCommandStatus.Proposal, proposal.Status);
            sql.Reset(); var clock = Stopwatch.StartNew(); var result = await commands.ApplySubroomReferenceRenameAsync(room, proposal.Proposal!, update); clock.Stop(); Assert.Equal(V2SubroomCommandStatus.Committed, result.Status);
            output.WriteLine($"V2 subroom-reference {phase} trace | sample={sample} | outcome={(update ? "update" : "do-not-update")} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
            if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"subroom-reference warm {update} sample {sample} exceeded the 50ms budget.");
        }
    }

    private async Task Samples(string phase, int count, RoomEditorV2CommandService commands, SqlTrace sql)
    {
        for (var i = 1; i <= count; i++)
        {
            var room = await RoomId();
            var existing = await ActiveSubroom(room);
            await Trace(phase, i, "text save", () => commands.SaveSubroomAsync(room, Baseline(existing), Draft(existing, $"V2 note {phase}-{i}")), sql);
            await Trace(phase, i, "geometry draw", () => commands.SaveSubroomGeometryAsync(room, Baseline(existing), 1, 2, 3, 4), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "geometry move", () => commands.SaveSubroomGeometryAsync(room, Baseline(existing), 2, 3, 3, 4), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "geometry resize", () => commands.SaveSubroomGeometryAsync(room, Baseline(existing), 2, 3, 5, 6), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "geometry removal", () => commands.SaveSubroomGeometryAsync(room, Baseline(existing), null, null, null, null), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "geometry redraw", () => commands.SaveSubroomGeometryAsync(room, Baseline(existing), 1, 2, 3, 4), sql);
            existing = await ActiveSubroom(room);
            await commands.HideSubroomAnnotationAsync(room, Baseline(existing)); // state preparation outside each measured operation
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "annotation show", () => commands.ShowSubroomAnnotationAsync(room, Baseline(existing)), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "annotation hide", () => commands.HideSubroomAnnotationAsync(room, Baseline(existing)), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "annotation clear", () => commands.ClearSubroomAnnotationAsync(room, Baseline(existing)), sql);
            await Trace(phase, i, "create", () => commands.CreateSubroomAsync(room, new(Guid.NewGuid(), $"V2 created {phase}-{i}", $"v2-created-{phase}-{i}", "complete", null, null, null, null)), sql);
            existing = await ActiveSubroom(room);
            await Trace(phase, i, "ordering active", () => commands.ReorderSubroomAsync(room, existing.Id, 1), sql);
            var archiveTarget = await ActiveSubroom(room);
            await Trace(phase, i, "archive", () => commands.SetSubroomArchiveAsync(room, archiveTarget.Id, true), sql);
            await Trace(phase, i, "ordering archived", () => commands.ReorderSubroomAsync(room, archiveTarget.Id, 0), sql);
            await Trace(phase, i, "restore", () => commands.SetSubroomArchiveAsync(room, archiveTarget.Id, false), sql);
            await commands.SetSubroomArchiveAsync(room, archiveTarget.Id, true); // preparation outside delete interval
            await Trace(phase, i, "permanent delete", () => commands.DeleteSubroomAsync(room, archiveTarget.Id), sql);
        }
    }

    private async Task Trace(string phase, int sample, string operation, Func<Task<V2SubroomCommandOutcome>> invoke, SqlTrace sql)
    {
        sql.Reset(); var clock = Stopwatch.StartNew(); var result = await invoke(); clock.Stop();
        Assert.Equal(V2SubroomCommandStatus.Committed, result.Status);
        output.WriteLine($"V2 subroom {phase} trace | sample={sample} | {operation} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
        if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"{operation} warm sample {sample} exceeded the 50ms direct-service budget.");
    }

    private async Task<Guid> RoomId() { await using var db = Context(); return await db.Rooms.OrderBy(x => x.SortOrder).Select(x => x.Id).FirstAsync(); }
    private async Task<Subroom> ActiveSubroom(Guid room) { await using var db = Context(); return await db.Subrooms.AsNoTracking().Where(x => x.RoomId == room && !x.IsArchived).OrderBy(x => x.SortOrder).FirstAsync(); }
    private static SubroomDurableBaseline Baseline(Subroom x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.ReferenceId, x.Notes ?? "", x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight);
    private static SubroomDraft Draft(Subroom x, string notes) => new(Guid.NewGuid(), x.FriendlyName, x.ReferenceId, notes, x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight);
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private static async Task SeedAsync(LogicDbContext db)
    {
        var groups = Enumerable.Range(0, 4).Select(i => new RoomGroup { FriendlyName = $"Group {i}", SortOrder = i }).ToArray(); db.AddRange(groups); await db.SaveChangesAsync();
        var rooms = Enumerable.Range(0, 58).Select(i => new Room { RoomGroupId = groups[i % 4].Id, FriendlyName = $"Room {i}", ReferenceId = $"room-{i}", SortOrder = i }).ToArray(); db.AddRange(rooms); await db.SaveChangesAsync();
        db.AddRange(Enumerable.Range(0, 78).Select(i => new Subroom { RoomId = rooms[i % 58].Id, FriendlyName = $"Subroom {i}", ReferenceId = $"subroom-{i}", SortOrder = i / 58 }));
        db.AddRange(Enumerable.Range(0, 159).Select(i => new RoomTransition { RoomId = rooms[i % 58].Id, FriendlyName = $"Transition {i}", Alias = $"E{i}", SortOrder = i / 58 }));
        db.AddRange(Enumerable.Range(0, 109).Select(i => new SubroomConnection { RoomId = rooms[i % 58].Id, FriendlyName = $"Connection {i}", Alias = $"C{i}", SortOrder = i / 58 }));
        db.AddRange(Enumerable.Range(0, 139).Select(i => new CheckLocation { RoomId = rooms[i % 58].Id, FriendlyName = $"Check {i}", SortOrder = i / 58 })); await db.SaveChangesAsync();
    }
    private sealed class Factory(string database, SqlTrace trace) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext() => Create(); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create()); private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={database}").AddInterceptors(trace).Options); }
    private sealed class SqlTrace : DbCommandInterceptor { private readonly Dictionary<DbCommand, Stopwatch> timers = []; public int Count { get; private set; } public TimeSpan Elapsed { get; private set; } public void Reset() { timers.Clear(); Count = 0; Elapsed = TimeSpan.Zero; } public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken t = default) { Start(c); return ValueTask.FromResult(r); } public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader r, CancellationToken t = default) { Stop(c); return ValueTask.FromResult(r); } public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken t = default) { Start(c); return ValueTask.FromResult(r); } public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken t = default) { Stop(c); return ValueTask.FromResult(r); } private void Start(DbCommand c) { Count++; timers[c] = Stopwatch.StartNew(); } private void Stop(DbCommand c) { if (timers.Remove(c, out var timer)) Elapsed += timer.Elapsed; } }
}
