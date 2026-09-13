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

/// <summary>Catalogue-scale direct V2 transition trace; page, map, and scene work are deliberately absent.</summary>
public sealed class RoomEditorV2TransitionServerTraceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int WarmSamples = 3; private readonly string path = Path.Combine(Path.GetTempPath(), $"silksong-v2-transition-trace-{Guid.NewGuid():N}.db");
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); await SeedAsync(db); await new LogicReferenceResolver(db).ResolveAsync(); }
    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsV2TransitionTextControlCreationOrderingAndLifecycleBreakdowns()
    {
        var sql = new SqlTrace(); var factory = new Factory(path, sql); var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        await Samples("sequence-first-use", 1, commands, sql); await Samples("warmup", 1, commands, sql); await Samples("warm", WarmSamples, commands, sql);
    }
    private async Task Samples(string phase, int count, RoomEditorV2CommandService commands, SqlTrace sql)
    {
        for (var i = 1; i <= count; i++)
        {
            var room = await RoomId(); var row = await Active(room);
            await Trace(phase, i, "text save", () => commands.SaveTransitionAsync(room, Baseline(row), Draft(row, $"note {phase}-{i}")), sql);
            row = await Active(room); await Trace(phase, i, "metadata save", () => commands.SaveTransitionMetadataAsync(room, Baseline(row), new($"game-{phase}-{i}", 1, 2, 3, 4, 5, 6, 7, 8)), sql);
            row = await Active(room); await Trace(phase, i, "annotation reset-to-game", () => commands.ResetTransitionAnnotationAsync(room, Baseline(row)), sql, V2AnnotationRefreshImpact.CanvasAndViewport);
            row = await Active(room); await Trace(phase, i, "annotation clear", () => commands.ClearTransitionAnnotationAsync(room, Baseline(row)), sql, V2AnnotationRefreshImpact.CanvasAndViewport);
            row = await Active(room); await Trace(phase, i, "annotation placement", () => commands.PlaceTransitionAnnotationAsync(room, Baseline(row), 20 + i, 30 + i), sql);
            row = await Active(room); await Trace(phase, i, "annotation drag", () => commands.MoveTransitionAnnotationAsync(room, Baseline(row), 21 + i, 31 + i), sql);
            row = await Active(room); await Trace(phase, i, "annotation hide", () => commands.RemoveTransitionAnnotationAsync(room, Baseline(row)), sql, V2AnnotationRefreshImpact.CanvasAndViewport);
            row = await Active(room); await Trace(phase, i, "annotation show", () => commands.ShowTransitionAnnotationAsync(room, Baseline(row)), sql, V2AnnotationRefreshImpact.CanvasAndViewport);
            row = await Active(room); await Trace(phase, i, "control save", () => commands.SaveTransitionAsync(room, Baseline(row), new(Guid.NewGuid(), row.Alias, row.FriendlyName, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.SourceSubroomReferenceText, row.DestinationRoomReferenceText, row.DestinationTransitionAliasText, row.Requirements, row.Notes, !row.IsTodo, row.IsVerified == true ? false : true)), sql);
            await Trace(phase, i, "create", () => commands.CreateTransitionAsync(room, new(Guid.NewGuid(), $"C{i}", $"created {phase}-{i}", null, null, null, null, null, null, null, null, null, null, null, null, "r", "n", false, null)), sql);
            var updateProposal = await PrepareCreateInverse(commands, room, $"IU-{phase}-{i}");
            await Trace(phase, i, "inverse update", () => commands.ApplyCreateInverseAsync(room, updateProposal, true), sql);
            var declineProposal = await PrepareCreateInverse(commands, room, $"ID-{phase}-{i}");
            await Trace(phase, i, "inverse do not update", () => commands.ApplyCreateInverseAsync(room, declineProposal, false), sql);
            row = await Active(room); await Trace(phase, i, "ordering active", () => commands.ReorderTransitionAsync(room, row.Id, 1), sql);
            var archive = await Active(room); await Trace(phase, i, "archive", () => commands.SetTransitionArchiveAsync(room, archive.Id, true), sql);
            await Trace(phase, i, "ordering archived", () => commands.ReorderTransitionAsync(room, archive.Id, 0), sql);
            await Trace(phase, i, "restore", () => commands.SetTransitionArchiveAsync(room, archive.Id, false), sql);
            await commands.SetTransitionArchiveAsync(room, archive.Id, true); await Trace(phase, i, "permanent delete", () => commands.DeleteTransitionAsync(room, archive.Id), sql);
        }
    }
    private async Task Trace(string phase, int sample, string operation, Func<Task<V2TransitionCommandOutcome>> call, SqlTrace sql, V2AnnotationRefreshImpact? expectedAnnotationImpact = null)
    {
        sql.Reset(); var clock = Stopwatch.StartNew(); var result = await call(); clock.Stop(); Assert.True(result.Status == V2TransitionCommandStatus.Committed, result.Message);
        if (expectedAnnotationImpact is { } expected) Assert.Equal(expected, result.AnnotationRefreshImpact);
        Assert.DoesNotContain(sql.Commands, command => command.Contains("\"Maps\"", StringComparison.Ordinal) || command.Contains("\"MapZones\"", StringComparison.Ordinal) || command.Contains("\"MapScenes\"", StringComparison.Ordinal) || command.Contains("\"MapChunks\"", StringComparison.Ordinal) || command.Contains("\"MapOverlays\"", StringComparison.Ordinal));
        output.WriteLine($"V2 transition {phase} trace | sample={sample} | {operation} | service={clock.Elapsed.TotalMilliseconds:F3}ms | sql={sql.Count} commands/{sql.Elapsed.TotalMilliseconds:F3}ms | resolver={result.ResolverElapsed.TotalMilliseconds:F3}ms | annotationImpact={result.AnnotationRefreshImpact} | map=0 | scene=0");
        if (phase == "warm") Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"{operation} warm sample {sample} exceeded the 50ms direct-service budget.");
    }
    private async Task<Guid> RoomId() { await using var db = Context(); return await db.Rooms.OrderBy(x => x.SortOrder).Select(x => x.Id).FirstAsync(); }
    private async Task<TransitionInverseCreateProposal> PrepareCreateInverse(RoomEditorV2CommandService commands, Guid sourceRoomId, string alias)
    {
        await using (var db = Context())
        {
            var targetRoom = await db.Rooms.OrderBy(x => x.SortOrder).Skip(1).FirstAsync();
            var next = await db.RoomTransitions.Where(x => x.RoomId == targetRoom.Id).Select(x => (int?)x.SortOrder).MaxAsync() is int last ? last + 1 : 0;
            db.RoomTransitions.Add(new() { RoomId = targetRoom.Id, Alias = alias, FriendlyName = alias, Requirements = "r", SortOrder = next });
            await db.SaveChangesAsync();
        }
        await using var read = Context(); var targetReference = await read.Rooms.OrderBy(x => x.SortOrder).Skip(1).Select(x => x.ReferenceId).FirstAsync();
        var result = await commands.CreateTransitionAsync(sourceRoomId, new(Guid.NewGuid(), alias, alias, null, null, null, null, null, null, null, null, null, null, targetReference, alias, "r", "n", false, null));
        Assert.Equal(V2TransitionCommandStatus.Proposal, result.Status);
        return result.CreateProposal!;
    }
    private async Task<RoomTransition> Active(Guid room) { await using var db = Context(); return await db.RoomTransitions.AsNoTracking().Where(x => x.RoomId == room && !x.IsArchived).OrderBy(x => x.SortOrder).FirstAsync(); }
    private static TransitionDurableBaseline Baseline(RoomTransition x) => new(x.Id,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.Alias,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SourceSubroomReferenceText,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText,x.Requirements,x.Notes,x.IsTodo,x.IsVerified,x.EnableAnnotation);
    private static TransitionDraft Draft(RoomTransition x, string notes) => new(Guid.NewGuid(),x.Alias,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SourceSubroomReferenceText,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText,x.Requirements,notes,x.IsTodo,x.IsVerified);
    private LogicDbContext Context() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    private static async Task SeedAsync(LogicDbContext db) { var groups=Enumerable.Range(0,4).Select(i=>new RoomGroup { FriendlyName=$"Group {i}",SortOrder=i}).ToArray(); db.AddRange(groups); await db.SaveChangesAsync(); var rooms=Enumerable.Range(0,58).Select(i=>new Room {RoomGroupId=groups[i%4].Id,FriendlyName=$"Room {i}",ReferenceId=$"room-{i}",SortOrder=i}).ToArray(); db.AddRange(rooms); await db.SaveChangesAsync(); db.AddRange(Enumerable.Range(0,78).Select(i=>new Subroom {RoomId=rooms[i%58].Id,FriendlyName=$"Subroom {i}",ReferenceId=$"subroom-{i}",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,159).Select(i=>new RoomTransition {RoomId=rooms[i%58].Id,FriendlyName=$"Transition {i}",Alias=$"E{i}",Requirements="r",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,109).Select(i=>new SubroomConnection {RoomId=rooms[i%58].Id,FriendlyName=$"Connection {i}",Alias=$"C{i}",SortOrder=i/58})); db.AddRange(Enumerable.Range(0,139).Select(i=>new CheckLocation {RoomId=rooms[i%58].Id,FriendlyName=$"Check {i}",Requirements="r",SortOrder=i/58})); await db.SaveChangesAsync(); }
    private sealed class Factory(string database, SqlTrace trace) : IDbContextFactory<LogicDbContext> { public LogicDbContext CreateDbContext()=>Create(); public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Create()); private LogicDbContext Create()=>new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={database}").AddInterceptors(trace).Options); }
    private sealed class SqlTrace : DbCommandInterceptor { private readonly Dictionary<DbCommand,Stopwatch> timers=[]; public int Count {get;private set;} public TimeSpan Elapsed {get;private set;} public List<string> Commands {get;}=[]; public void Reset(){timers.Clear();Commands.Clear();Count=0;Elapsed=TimeSpan.Zero;} public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken t=default){Start(c);return ValueTask.FromResult(r);} public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken t=default){Stop(c);return ValueTask.FromResult(r);} public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken t=default){Start(c);return ValueTask.FromResult(r);} public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken t=default){Stop(c);return ValueTask.FromResult(r);} private void Start(DbCommand c){Count++;Commands.Add(c.CommandText);timers[c]=Stopwatch.StartNew();} private void Stop(DbCommand c){if(timers.Remove(c,out var timer))Elapsed+=timer.Elapsed;} }
}
