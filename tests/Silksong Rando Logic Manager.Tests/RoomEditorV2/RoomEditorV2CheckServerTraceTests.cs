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

/// <summary>Catalogue-scale direct V2 check trace; page refresh/rendering is excluded.</summary>
public sealed class RoomEditorV2CheckServerTraceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int WarmSamples = 3;
    private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-v2-check-trace-{Guid.NewGuid():N}.db");
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); await Seed(db); await new LogicReferenceResolver(db).ResolveAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsV2CheckTextControlCreationOrderingAndLifecycleBreakdowns()
    {
        var sql = new SqlTrace(); var commands = new RoomEditorV2CommandService(new LogicCatalogService(new Factory(path, sql)));
        await Samples("sequence-first-use", 1, commands, sql); // serial diagnostic pass: only first is cold
        await Samples("warmup", 1, commands, sql); // all adapter/query shapes outside compliance timing
        await Samples("warm", WarmSamples, commands, sql);
    }

    private async Task Samples(string phase, int count, RoomEditorV2CommandService commands, SqlTrace sql)
    {
        for (var i = 1; i <= count; i++)
        {
            var room = await RoomId(); var check = await ActiveCheck(room);
            await Trace(phase, i, "text save", () => commands.SaveCheckAsync(room, Baseline(check), Draft(check, $"V2 note {phase}-{i}")), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "control save", () => commands.SaveCheckAsync(room, Baseline(check), ControlDraft(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "metadata save", () => commands.SaveCheckMetadataAsync(room, MetadataBaseline(check), new($"check-game-{phase}-{i}", 1, 2, 3, 4, 5, 6, 7, 8)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation reset", () => commands.ResetCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation clear", () => commands.ClearCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation reset after clear", () => commands.ResetCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation hide", () => commands.RemoveCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation show after hide", () => commands.ShowCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation placement", () => commands.PlaceCheckAnnotationAsync(room, MetadataBaseline(check), 20 + i, 30 + i), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation drag", () => commands.MoveCheckAnnotationAsync(room, MetadataBaseline(check), 21 + i, 31 + i), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation hide after drag", () => commands.RemoveCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "annotation show", () => commands.ShowCheckAnnotationAsync(room, MetadataBaseline(check)), sql);
            await Trace(phase, i, "create", () => commands.CreateCheckAsync(room, new(Guid.NewGuid(), $"V2 created {phase}-{i}", null, "requirement", "note", false, true, false)), sql);
            check = await ActiveCheck(room);
            await Trace(phase, i, "ordering active", () => commands.ReorderCheckAsync(room, check.Id, 1), sql);
            var archive = await ActiveCheck(room);
            await Trace(phase, i, "archive", () => commands.SetCheckArchiveAsync(room, archive.Id, true), sql);
            await Trace(phase, i, "ordering archived", () => commands.ReorderCheckAsync(room, archive.Id, 0), sql);
            await Trace(phase, i, "restore", () => commands.SetCheckArchiveAsync(room, archive.Id, false), sql);
            await commands.SetCheckArchiveAsync(room, archive.Id, true);
            await Trace(phase, i, "permanent delete", () => commands.DeleteCheckAsync(room, archive.Id), sql);
        }
    }
    private async Task Trace(string phase, int sample, string operation, Func<Task<V2CheckCommandOutcome>> call, SqlTrace sql)
    {
        sql.Reset(); var clock = Stopwatch.StartNew(); var result = await call(); clock.Stop(); Assert.Equal(V2CheckCommandStatus.Committed, result.Status);
        Assert.DoesNotContain(sql.Commands, command => command.Contains("\"Maps\"", StringComparison.Ordinal) || command.Contains("\"MapZones\"", StringComparison.Ordinal) || command.Contains("\"MapScenes\"", StringComparison.Ordinal) || command.Contains("\"MapChunks\"", StringComparison.Ordinal) || command.Contains("\"MapOverlays\"", StringComparison.Ordinal));
        output.WriteLine($"V2 check {phase} trace | sample={sample} | {operation} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | impact={result.Status} | map=0 | scene=0");
        if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"{operation} warm sample {sample} exceeded the 50ms direct-service budget.");
    }
    private async Task<Guid> RoomId() { await using var db = Context(); return await db.Rooms.OrderBy(x => x.SortOrder).Select(x => x.Id).FirstAsync(); }
    private async Task<CheckLocation> ActiveCheck(Guid room) { await using var db = Context(); return await db.CheckLocations.AsNoTracking().Where(x => x.RoomId == room && !x.IsArchived).OrderBy(x => x.SortOrder).FirstAsync(); }
    private static CheckDurableBaseline Baseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.IsIncludedInApworld, x.IsTodo, x.IsVerified);
    private static CheckMetadataDurableBaseline MetadataBaseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.IsIncludedInApworld, x.EnableAnnotation, x.IsTodo, x.IsVerified, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static CheckDraft Draft(CheckLocation x, string notes) => new(Guid.NewGuid(), x.FriendlyName, x.SubroomReferenceText, x.Requirements, notes, x.IsIncludedInApworld, x.IsTodo, x.IsVerified);
    private static CheckDraft ControlDraft(CheckLocation x) => new(Guid.NewGuid(), x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, !x.IsIncludedInApworld, !x.IsTodo, x.IsVerified == true ? false : true);
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private static async Task Seed(LogicDbContext db) { var groups=Enumerable.Range(0,4).Select(i=>new RoomGroup { FriendlyName=$"Group {i}",SortOrder=i}).ToArray(); db.AddRange(groups); await db.SaveChangesAsync(); var rooms=Enumerable.Range(0,58).Select(i=>new Room {RoomGroupId=groups[i%4].Id,FriendlyName=$"Room {i}",ReferenceId=$"room-{i}",SortOrder=i}).ToArray(); db.AddRange(rooms); await db.SaveChangesAsync(); db.AddRange(Enumerable.Range(0,78).Select(i=>new Subroom {RoomId=rooms[i%58].Id,FriendlyName=$"Subroom {i}",ReferenceId=$"subroom-{i}",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,159).Select(i=>new RoomTransition {RoomId=rooms[i%58].Id,FriendlyName=$"Transition {i}",Alias=$"E{i}",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,109).Select(i=>new SubroomConnection {RoomId=rooms[i%58].Id,FriendlyName=$"Connection {i}",Alias=$"C{i}",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,139).Select(i=>new CheckLocation {RoomId=rooms[i%58].Id,FriendlyName=$"Check {i}",Requirements="r",SortOrder=i/58})); await db.SaveChangesAsync(); }
    private sealed class Factory(string database, SqlTrace trace) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext()=>Create(); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Create()); private LogicDbContext Create()=>new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={database}").AddInterceptors(trace).Options); }
    private sealed class SqlTrace : DbCommandInterceptor { private readonly Dictionary<DbCommand,Stopwatch> timers=[]; public int Count {get;private set;} public TimeSpan Elapsed {get;private set;} public List<string> Commands {get;}=[]; public void Reset(){timers.Clear();Commands.Clear();Count=0;Elapsed=TimeSpan.Zero;} public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken t=default){Start(c);return ValueTask.FromResult(r);} public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken t=default){Stop(c);return ValueTask.FromResult(r);} public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken t=default){Start(c);return ValueTask.FromResult(r);} public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken t=default){Stop(c);return ValueTask.FromResult(r);} private void Start(DbCommand c){Count++;Commands.Add(c.CommandText);timers[c]=Stopwatch.StartNew();} private void Stop(DbCommand c){if(timers.Remove(c,out var timer))Elapsed+=timer.Elapsed;} }
}
