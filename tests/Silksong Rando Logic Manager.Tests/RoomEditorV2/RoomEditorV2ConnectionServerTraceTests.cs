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

/// <summary>Catalogue-scale direct V2 connection trace; page, map, and scene work are deliberately absent.</summary>
public sealed class RoomEditorV2ConnectionServerTraceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int WarmSamples = 3; private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-v2-connection-trace-{Guid.NewGuid():N}.db"); private int scaffoldSequence, placementSequence;
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); await SeedAsync(db); await new LogicReferenceResolver(db).ResolveAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsV2ConnectionTextCreationScaffoldOrderingAndLifecycleBreakdowns()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        await Samples("sequence-first-use", 1, commands, sql); await Samples("warmup", 1, commands, sql); await Samples("warm", WarmSamples, commands, sql);
    }
    private async Task Samples(string phase, int count, RoomEditorV2CommandService commands, SqlTrace sql)
    {
        for (var i = 1; i <= count; i++)
        {
            var room = await RoomId(); var row = await Active(room);
            await Trace(phase, i, "text save", () => commands.SaveConnectionAsync(room, Baseline(row), Draft(row, $"note {phase}-{i}")), sql);
            await Trace(phase, i, "create", () => commands.CreateConnectionAsync(room, new(Guid.NewGuid(), $"C{i}", $"created {phase}-{i}", "", "", "r", "n", false, null, null, false, null)), sql);
            var scaffoldSource = await PrepareScaffoldSourceAsync(room, phase, i);
            await Trace(phase, i, "inverse scaffold", () => commands.ScaffoldInverseConnectionAsync(room, scaffoldSource), sql);
            var placementSource = await PreparePlacementSourceAsync(room, phase, i);
            await Trace(phase, i, "annotation placement", () => commands.PlaceConnectionAnnotationAsync(room, placementSource, 20 + i, 30 + i), sql);
            await Trace(phase, i, "annotation move", () => commands.MoveConnectionAnnotationAsync(room, placementSource, 21 + i, 31 + i), sql);
            await Trace(phase, i, "annotation disable", () => commands.DisableConnectionAnnotationAsync(room, placementSource), sql);
            await Trace(phase, i, "annotation show", () => commands.ShowConnectionAnnotationAsync(room, placementSource), sql);
            await Trace(phase, i, "annotation clear", () => commands.ClearConnectionAnnotationAsync(room, placementSource), sql);
            row = await Active(room); await Trace(phase, i, "ordering active", () => commands.ReorderConnectionAsync(room, row.Id, 1), sql);
            var archive = await Active(room); await Trace(phase, i, "archive", () => commands.SetConnectionArchiveAsync(room, archive.Id, true), sql);
            await Trace(phase, i, "restore", () => commands.SetConnectionArchiveAsync(room, archive.Id, false), sql);
            await commands.SetConnectionArchiveAsync(room, archive.Id, true); await Trace(phase, i, "permanent delete", () => commands.DeleteConnectionAsync(room, archive.Id), sql);
        }
    }
    private async Task Trace(string phase, int sample, string operation, Func<Task<V2ConnectionCommandOutcome>> call, SqlTrace sql)
    {
        sql.Reset(); var clock = Stopwatch.StartNew(); var result = await call(); clock.Stop(); Assert.Equal(V2ConnectionCommandStatus.Committed, result.Status);
        Assert.DoesNotContain(sql.Commands, command => command.Contains("\"Maps\"", StringComparison.Ordinal) || command.Contains("\"MapZones\"", StringComparison.Ordinal) || command.Contains("\"MapScenes\"", StringComparison.Ordinal) || command.Contains("\"MapChunks\"", StringComparison.Ordinal) || command.Contains("\"MapOverlays\"", StringComparison.Ordinal));
        output.WriteLine($"V2 connection {phase} trace | sample={sample} | {operation} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
        if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"{operation} warm sample {sample} exceeded the 50ms direct-service budget.");
    }
    private async Task<Guid> RoomId() { await using var db = Context(); return await db.Rooms.OrderBy(x => x.SortOrder).Select(x => x.Id).FirstAsync(); }
    private async Task<SubroomConnection> Active(Guid room) { await using var db = Context(); return await db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == room && !x.IsArchived).OrderBy(x => x.SortOrder).FirstAsync(); }
    private async Task<Guid> PrepareScaffoldSourceAsync(Guid roomId, string phase, int sample)
    {
        await using var db = Context();
        var endpoints = await db.Subrooms.Where(x => x.RoomId == roomId && !x.IsArchived).OrderBy(x => x.SortOrder).Take(2).ToArrayAsync();
        var source = new SubroomConnection { RoomId = roomId, Alias = $"s{++scaffoldSequence:D2}", FriendlyName = $"scaffold {phase}-{sample}", SourceSubroomReferenceText = endpoints[0].ReferenceId, DestinationSubroomReferenceText = endpoints[1].ReferenceId, Requirements = "r", SortOrder = await db.SubroomConnections.Where(x => x.RoomId == roomId && !x.IsArchived).CountAsync() };
        db.Add(source); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync();
        return source.Id;
    }
    private async Task<Guid> PreparePlacementSourceAsync(Guid roomId, string phase, int sample)
    {
        await using var db = Context();
        var endpoints = await db.Subrooms.Where(x => x.RoomId == roomId && !x.IsArchived).OrderBy(x => x.SortOrder).Take(2).ToArrayAsync();
        var source = new SubroomConnection { RoomId = roomId, Alias = $"p{++placementSequence:D2}", FriendlyName = $"placement {phase}-{sample}", SourceSubroomReferenceText = endpoints[0].ReferenceId, DestinationSubroomReferenceText = endpoints[1].ReferenceId, Requirements = "r", SortOrder = await db.SubroomConnections.Where(x => x.RoomId == roomId && !x.IsArchived).CountAsync() };
        db.Add(source); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync();
        return source.Id;
    }
    private static ConnectionDurableBaseline Baseline(SubroomConnection x) => new(x.Id,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.Alias,x.FriendlyName,x.SourceSubroomReferenceText,x.DestinationSubroomReferenceText,x.Requirements,x.Notes,x.EnableAnnotation,x.SceneUnitX,x.SceneUnitY,x.IsTodo,x.IsVerified);
    private static ConnectionDraft Draft(SubroomConnection x, string notes) => new(Guid.NewGuid(),x.Alias,x.FriendlyName,x.SourceSubroomReferenceText,x.DestinationSubroomReferenceText,x.Requirements,notes,x.EnableAnnotation,x.SceneUnitX,x.SceneUnitY,x.IsTodo,x.IsVerified);
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private static async Task SeedAsync(LogicDbContext db) { var groups=Enumerable.Range(0,4).Select(i=>new RoomGroup { FriendlyName=$"Group {i}",SortOrder=i}).ToArray(); db.AddRange(groups); await db.SaveChangesAsync(); var rooms=Enumerable.Range(0,58).Select(i=>new Room {RoomGroupId=groups[i%4].Id,FriendlyName=$"Room {i}",ReferenceId=$"room-{i}",SortOrder=i}).ToArray(); db.AddRange(rooms); await db.SaveChangesAsync(); db.AddRange(Enumerable.Range(0,78).Select(i=>new Subroom {RoomId=rooms[i%58].Id,FriendlyName=$"Subroom {i}",ReferenceId=$"subroom-{i}",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,159).Select(i=>new RoomTransition {RoomId=rooms[i%58].Id,FriendlyName=$"Transition {i}",Alias=$"E{i}",Requirements="r",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,109).Select(i=>new SubroomConnection {RoomId=rooms[i%58].Id,FriendlyName=$"Connection {i}",Alias=$"C{i}",Requirements="r",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,139).Select(i=>new CheckLocation {RoomId=rooms[i%58].Id,FriendlyName=$"Check {i}",Requirements="r",SortOrder=i/58})); await db.SaveChangesAsync(); }
    private sealed class Factory(string database, SqlTrace trace) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext()=>Create(); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Create()); private LogicDbContext Create()=>new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={database}").AddInterceptors(trace).Options); }
    private sealed class SqlTrace : DbCommandInterceptor { private readonly Dictionary<DbCommand,Stopwatch> timers=[]; public int Count {get;private set;} public TimeSpan Elapsed {get;private set;} public List<string> Commands {get;}=[]; public void Reset(){timers.Clear();Commands.Clear();Count=0;Elapsed=TimeSpan.Zero;} public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken t=default){Start(c);return ValueTask.FromResult(r);} public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken t=default){Stop(c);return ValueTask.FromResult(r);} public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken t=default){Start(c);return ValueTask.FromResult(r);} public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken t=default){Stop(c);return ValueTask.FromResult(r);} private void Start(DbCommand c){Count++;Commands.Add(c.CommandText);timers[c]=Stopwatch.StartNew();} private void Stop(DbCommand c){if(timers.Remove(c,out var timer))Elapsed+=timer.Elapsed;} }
}
