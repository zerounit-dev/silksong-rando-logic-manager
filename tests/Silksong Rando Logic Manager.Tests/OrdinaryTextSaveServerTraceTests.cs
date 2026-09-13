using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;
using Xunit.Abstractions;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class OrdinaryTextSaveServerTraceTests : IAsyncLifetime
{
    private const int WarmSampleCount = 3;
    private readonly ITestOutputHelper output;
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-ordinary-text-trace-{Guid.NewGuid():N}.db");
    private int scaffoldAliasSequence;

    public OrdinaryTextSaveServerTraceTests(ITestOutputHelper output) => this.output = output;

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await SeedFixtureAsync(db);
        await new LogicReferenceResolver(db).ResolveAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SequenceFirstUseAndWarmTrace_RecordsOrdinaryTextAndReferenceSaveBreakdowns()
    {
        var trace = new SqlTraceInterceptor();
        var resolverTrace = new ScopedResolverTrace();
        var factory = new TraceDbContextFactory(databasePath, trace);
        var catalog = new LogicCatalogService(factory, scopedResolverTrace: resolverTrace);

        var sequenceFirstUseOutcome = await TraceSamplesAsync("sequence-first-use", 1, catalog, trace, resolverTrace);

        // Warm EF, SQLite, and every direct-save/resolver query shape outside recorded samples.
        await WarmScopedQueryShapesAsync(catalog);
        var warmOutcome = await TraceSamplesAsync("warm", WarmSampleCount, catalog, trace, resolverTrace);

        var roomDocumentRefresh = Stopwatch.StartNew();
        _ = await GetFirstRoomDocumentAsync(catalog);
        roomDocumentRefresh.Stop();

        Assert.True(sequenceFirstUseOutcome.Changed);
        Assert.True(sequenceFirstUseOutcome.Resolved);
        Assert.False(sequenceFirstUseOutcome.RequiresDocumentRefresh);
        Assert.True(warmOutcome.Changed);
        Assert.True(warmOutcome.Resolved);
        Assert.False(warmOutcome.RequiresDocumentRefresh);
        output.WriteLine($"ordinary text/reference orchestration | room-document refresh={roomDocumentRefresh.Elapsed.TotalMilliseconds:F3}ms");

        await using var verification = CreateContext();
        var persisted = await verification.RoomTransitions.SingleAsync(item => item.Id == warmOutcome.TransitionId);
        Assert.Equal("room-2", persisted.DestinationRoomReferenceText);
        Assert.NotNull(persisted.ResolvedDestinationRoomId);
        Assert.NotNull(persisted.ResolvedDestinationTransitionId);
    }

    [Fact]
    public async Task CreationSequenceFirstUseAndWarmTrace_RecordsEveryChildCreationShape()
    {
        var trace = new SqlTraceInterceptor();
        var resolverTrace = new ScopedResolverTrace();
        var factory = new TraceDbContextFactory(databasePath, trace);
        var catalog = new LogicCatalogService(factory, scopedResolverTrace: resolverTrace);

        // This is one serial diagnostic sequence: only its first operation is cold.
        await TraceCreationSamplesAsync("sequence-first-use", 1, catalog, trace);

        // Warm every direct creation and scoped-created-resolver query shape outside timing.
        await WarmCreationQueryShapesAsync(catalog);
        await TraceCreationSamplesAsync("warm", WarmSampleCount, catalog, trace);

        await using var verification = CreateContext();
        Assert.True(await verification.Subrooms.CountAsync() >= 78 + 5);
        Assert.True(await verification.RoomTransitions.CountAsync() >= 159 + 5);
        Assert.True(await verification.SubroomConnections.CountAsync() >= 109 + 5);
        Assert.True(await verification.CheckLocations.CountAsync() >= 139 + 5);
    }

    [Fact]
    public async Task ToggleSelectAndCheckLayoutGeometrySequenceFirstUseAndWarmTrace_RecordsCommittedBreakdowns()
    {
        var trace = new SqlTraceInterceptor();
        var factory = new TraceDbContextFactory(databasePath, trace);
        var catalog = new LogicCatalogService(factory);

        await TraceCheckToggleAndGeometrySamplesAsync("sequence-first-use", 1, catalog, trace);
        // Warm each direct patch shape outside the compliance interval.
        await TraceCheckToggleAndGeometrySamplesAsync("warmup", 1, catalog, trace);
        await TraceCheckToggleAndGeometrySamplesAsync("warm", WarmSampleCount, catalog, trace);
    }

    private async Task TraceCreationSamplesAsync(string phase, int sampleCount, LogicCatalogService catalog, SqlTraceInterceptor trace)
    {
        var roomId = await GetFirstRoomIdAsync();
        for (var sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
        {
            var suffix = $"{phase}-{sampleIndex}";
            await TraceCreateAsync(phase, sampleIndex, "group placeholder", () => catalog.CreateRoomGroupWithOutcomeAsync("New group"), trace);
            await TraceCreateAsync(phase, sampleIndex, "room placeholder", () => catalog.CreateRoomWithOutcomeAsync("New room", null), trace);
            var subroom = await TraceCreateAsync(phase, sampleIndex, "subroom", () => catalog.CreateSubroomWithOutcomeAsync(roomId, new Subroom { FriendlyName = $"Created subroom {suffix}", ReferenceId = $"created-subroom-{suffix}", Notes = "complete snapshot" }), trace);
            var transition = await TraceCreateAsync(phase, sampleIndex, "transition", () => catalog.CreateTransitionWithOutcomeAsync(roomId, new RoomTransition { Alias = $"T{sampleIndex}", FriendlyName = $"Created transition {suffix}", SourceSubroomReferenceText = subroom.ReferenceId, DestinationRoomReferenceText = "room-2", DestinationTransitionAliasText = "E2", Requirements = "requires dash", Notes = "complete snapshot" }), trace);
            var connection = await TraceCreateAsync(phase, sampleIndex, "connection", () => catalog.CreateConnectionWithOutcomeAsync(roomId, new SubroomConnection { Alias = $"C{sampleIndex}", FriendlyName = $"Created connection {suffix}", SourceSubroomReferenceText = subroom.ReferenceId, DestinationSubroomReferenceText = subroom.ReferenceId, Requirements = "requires dash", Notes = "complete snapshot" }), trace);
            var check = await TraceCreateAsync(phase, sampleIndex, "check", () => catalog.CreateCheckWithOutcomeAsync(roomId, new CheckLocation { FriendlyName = $"Created check {suffix}", SubroomReferenceText = subroom.ReferenceId, Requirements = "requires dash", Notes = "complete snapshot", IsIncludedInApworld = false, IsTodo = true }), trace);
            var scaffoldSourceId = await PrepareScaffoldSourceAsync(roomId, suffix);
            var inverse = await TraceCreateAsync(phase, sampleIndex, "inverse scaffold", () => catalog.ScaffoldInverseConnectionWithOutcomeAsync(scaffoldSourceId), trace);
            Assert.Null(transition.IsVerified);
            Assert.Null(connection.IsVerified);
            Assert.Null(check.IsVerified);
            Assert.Null(inverse.IsVerified);
        }
    }

    private async Task TraceCheckToggleAndGeometrySamplesAsync(string phase, int sampleCount, LogicCatalogService catalog, SqlTraceInterceptor trace)
    {
        for (var sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
        {
            var document = await GetFirstRoomDocumentAsync(catalog);
            var check = document.Checks[0];
            check.IsTodo = !check.IsTodo;
            await TraceCheckPatchAsync(phase, sampleIndex, "check TODO toggle", () => catalog.SaveCheckWithPatchAsync(check, document.Room.Id), trace);
            check.IsIncludedInApworld = !check.IsIncludedInApworld;
            await TraceCheckPatchAsync(phase, sampleIndex, "check APWorld toggle", () => catalog.SaveCheckWithPatchAsync(check, document.Room.Id), trace);
            check.IsVerified = check.IsVerified switch { false => null, null => true, _ => false };
            await TraceCheckPatchAsync(phase, sampleIndex, "check verification select", () => catalog.SaveCheckWithPatchAsync(check, document.Room.Id), trace);
            check.EnableAnnotation = !check.EnableAnnotation;
            await TraceCheckPatchAsync(phase, sampleIndex, "check annotation enablement", () => catalog.SaveCheckWithPatchAsync(check, document.Room.Id), trace);
            check.AnnotationSceneUnitX = sampleIndex + .25;
            check.AnnotationSceneUnitY = sampleIndex + .5;
            await TraceCheckPatchAsync(phase, sampleIndex, "check marker geometry", () => catalog.SaveCheckWithPatchAsync(check, document.Room.Id), trace);
        }
    }

    private async Task WarmCreationQueryShapesAsync(LogicCatalogService catalog)
    {
        var roomId = await GetFirstRoomIdAsync();
        await catalog.CreateRoomGroupWithOutcomeAsync("New group");
        await catalog.CreateRoomWithOutcomeAsync("New room", null);
        var subroom = (await catalog.CreateSubroomWithOutcomeAsync(roomId, new Subroom { FriendlyName = "Warm created subroom", ReferenceId = "warm-created-subroom", Notes = "complete snapshot" })).Entity;
        await catalog.CreateTransitionWithOutcomeAsync(roomId, new RoomTransition { Alias = "TW", FriendlyName = "Warm created transition", SourceSubroomReferenceText = subroom.ReferenceId, DestinationRoomReferenceText = "room-2", DestinationTransitionAliasText = "E2", Requirements = "requires dash", Notes = "complete snapshot" });
        await catalog.CreateConnectionWithOutcomeAsync(roomId, new SubroomConnection { Alias = "CW", FriendlyName = "Warm created connection", SourceSubroomReferenceText = subroom.ReferenceId, DestinationSubroomReferenceText = subroom.ReferenceId, Requirements = "requires dash", Notes = "complete snapshot" });
        await catalog.CreateCheckWithOutcomeAsync(roomId, new CheckLocation { FriendlyName = "Warm created check", SubroomReferenceText = subroom.ReferenceId, Requirements = "requires dash", Notes = "complete snapshot", IsIncludedInApworld = false, IsTodo = true });
        await catalog.ScaffoldInverseConnectionWithOutcomeAsync(await PrepareScaffoldSourceAsync(roomId, "warm"));
    }

    private async Task<Guid> PrepareScaffoldSourceAsync(Guid roomId, string suffix)
    {
        await using var db = CreateContext();
        var source = new Subroom { RoomId = roomId, FriendlyName = $"Scaffold source {suffix}", ReferenceId = $"scaffold-source-{suffix}", SortOrder = await db.Subrooms.Where(item => item.RoomId == roomId).Select(item => (int?)item.SortOrder).MaxAsync() + 1 ?? 0 };
        var destination = new Subroom { RoomId = roomId, FriendlyName = $"Scaffold destination {suffix}", ReferenceId = $"scaffold-destination-{suffix}", SortOrder = source.SortOrder + 1 };
        db.AddRange(source, destination);
        await db.SaveChangesAsync();
        var connection = new SubroomConnection { RoomId = roomId, Alias = NextScaffoldAlias(), FriendlyName = $"Scaffold {suffix}", SourceSubroomReferenceText = source.ReferenceId, DestinationSubroomReferenceText = destination.ReferenceId, SortOrder = await db.SubroomConnections.Where(item => item.RoomId == roomId).Select(item => (int?)item.SortOrder).MaxAsync() + 1 ?? 0 };
        db.SubroomConnections.Add(connection);
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        return connection.Id;
    }

    private string NextScaffoldAlias()
    {
        var value = scaffoldAliasSequence++;
        if (value >= 36 * 36) throw new InvalidOperationException("The trace exhausted its deterministic scaffold aliases.");
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return $"S{alphabet[value / 36]}{alphabet[value % 36]}";
    }

    private async Task<TEntity> TraceCreateAsync<TEntity>(string phase, int sampleIndex, string sample, Func<Task<CatalogCreateOutcome<TEntity>>> create, SqlTraceInterceptor trace) where TEntity : AuditedEntity
    {
        trace.Reset();
        var stopwatch = Stopwatch.StartNew();
        var outcome = await create();
        stopwatch.Stop();
        output.WriteLine($"creation {phase} trace | sample={sampleIndex} | {sample} | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | sql={trace.CommandCount} commands/{trace.Elapsed.TotalMilliseconds:F3}ms | resolver={outcome.ResolverElapsed.TotalMilliseconds:F3}ms");
        if (phase == "warm") Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"{sample} warm sample {sampleIndex} exceeded the 50ms direct service budget.");
        return outcome.Entity;
    }

    private async Task<TracePhaseOutcome> TraceSamplesAsync(string phase, int sampleCount, LogicCatalogService catalog, SqlTraceInterceptor trace, ScopedResolverTrace resolverTrace)
    {
        CatalogSaveOutcome? destinationAliasOutcome = null;
        Guid transitionId = default;

        for (var sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
        {
            var document = await GetFirstRoomDocumentAsync(catalog);
            var note = document.Subrooms[0];
            note.Notes = $"{phase} trace text {sampleIndex}";
            await TraceAsync(phase, sampleIndex, "subroom notes", () => catalog.SaveWithOutcomeAsync(note), trace, resolverTrace);

            var room = document.Room;
            room.ReferenceId = $"room-1-{phase}-{sampleIndex}";
            await TraceAsync(phase, sampleIndex, "room reference rename", () => catalog.SaveWithOutcomeAsync(room), trace, resolverTrace);

            var subroom = document.Subrooms[0];
            subroom.ReferenceId = $"subroom-1-{phase}-{sampleIndex}";
            await TraceAsync(phase, sampleIndex, "subroom reference rename", () => catalog.SaveWithOutcomeAsync(subroom), trace, resolverTrace);

            document = await GetFirstRoomDocumentAsync(catalog);
            var reference = document.Transitions[0];
            var transitionBaseline = TransitionFieldDiffMapper.Clone(reference);
            reference.SourceSubroomReferenceText = subroom.ReferenceId;
            reference = (await TraceTransitionAsync(phase, sampleIndex, "transition source subroom", () => catalog.SaveTransitionWithPatchAsync(transitionBaseline, reference, document.Room.Id), trace, resolverTrace)).SavedRow!;
            transitionBaseline = TransitionFieldDiffMapper.Clone(reference);
            reference.DestinationRoomReferenceText = TrimmedReferenceForSample("room-2", sampleIndex);
            reference = (await TraceTransitionAsync(phase, sampleIndex, "transition destination room", () => catalog.SaveTransitionWithPatchAsync(transitionBaseline, reference, document.Room.Id), trace, resolverTrace)).SavedRow!;
            transitionBaseline = TransitionFieldDiffMapper.Clone(reference);
            reference.DestinationTransitionAliasText = TrimmedReferenceForSample("E2", sampleIndex);
            var destinationPatch = await TraceTransitionAsync(phase, sampleIndex, "transition destination alias", () => catalog.SaveTransitionWithPatchAsync(transitionBaseline, reference, document.Room.Id), trace, resolverTrace);
            destinationAliasOutcome = destinationPatch.Outcome;
            reference = destinationPatch.SavedRow!;
            transitionId = reference.Id;

            transitionBaseline = TransitionFieldDiffMapper.Clone(reference);
            reference.Alias = $"E1-{phase}-{sampleIndex}";
            await TraceTransitionAsync(phase, sampleIndex, "transition alias inbound", () => catalog.SaveTransitionWithPatchAsync(transitionBaseline, reference, document.Room.Id), trace, resolverTrace);
            transitionBaseline = TransitionFieldDiffMapper.Clone(reference);
            reference.Requirements = $"requirements {phase} {sampleIndex}";
            await TraceTransitionAsync(phase, sampleIndex, "transition requirements", () => catalog.SaveTransitionWithPatchAsync(transitionBaseline, reference, document.Room.Id), trace, resolverTrace);
            var connection = document.Connections[0];
            var connectionSaves = new ConnectionRoomSaveCoordinator(catalog);
            var connectionBaseline = new ConnectionFieldDiffMapper().Clone(connection);
            connection.SourceSubroomReferenceText = subroom.ReferenceId;
            var connectionPatch = await TraceConnectionAsync(phase, sampleIndex, "connection source", () => connectionSaves.SaveAsync(connectionBaseline, connection, document.Room.Id), trace, resolverTrace);
            connection = connectionPatch.SavedRow!;
            connectionBaseline = new ConnectionFieldDiffMapper().Clone(connection);
            connection.DestinationSubroomReferenceText = subroom.ReferenceId;
            connection = (await TraceConnectionAsync(phase, sampleIndex, "connection destination", () => connectionSaves.SaveAsync(connectionBaseline, connection, document.Room.Id), trace, resolverTrace)).SavedRow!;
            connectionBaseline = new ConnectionFieldDiffMapper().Clone(connection);
            connection.Requirements = $"requirements {phase} {sampleIndex}";
            await TraceConnectionAsync(phase, sampleIndex, "connection requirements", () => connectionSaves.SaveAsync(connectionBaseline, connection, document.Room.Id), trace, resolverTrace);
            var check = document.Checks[0];
            check.SubroomReferenceText = subroom.ReferenceId;
            await TraceAsync(phase, sampleIndex, "check subroom", () => catalog.SaveWithOutcomeAsync(check), trace, resolverTrace);
            var checkBaseline = new CheckFieldDiffMapper().Clone(check);
            check.Requirements = $"requirements {phase} {sampleIndex}";
            await TraceCheckAsync(phase, sampleIndex, "check requirements", () => catalog.SaveCheckWithPatchAsync(checkBaseline, check, document.Room.Id), trace);
            var groupId = (await catalog.GetWorkspaceAsync()).RoomGroups[0].Id;
            var group = (await catalog.GetGroupEditorDataAsync(groupId))!.Group;
            group.ZoneReferenceText = TrimmedReferenceForSample("zone", sampleIndex);
            await TraceAsync(phase, sampleIndex, "group zone", () => catalog.SaveWithOutcomeAsync(group), trace, resolverTrace);
        }

        return new TracePhaseOutcome(destinationAliasOutcome ?? throw new InvalidOperationException("No destination-alias sample was recorded."), transitionId);
    }

    private async Task<CatalogSaveOutcome> TraceAsync(string phase, int sampleIndex, string sample, Func<Task<CatalogSaveOutcome>> save, SqlTraceInterceptor trace, ScopedResolverTrace resolverTrace)
    {
        trace.Reset();
        resolverTrace.Reset();
        var stopwatch = Stopwatch.StartNew();
        var outcome = await save();
        stopwatch.Stop();
        output.WriteLine($"ordinary text/reference {phase} trace | sample={sampleIndex} | {sample} | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | sql={trace.CommandCount} commands/{trace.Elapsed.TotalMilliseconds:F3}ms | resolver={outcome.ResolverElapsed.TotalMilliseconds:F3}ms");
        if (sample == "subroom reference rename") output.WriteLine($"subroom resolver stages | phase={phase} | sample={sampleIndex} | {string.Join(" | ", resolverTrace.Stages.Select(item => $"{item.Key}={item.Value.TotalMilliseconds:F3}ms"))}");
        if (phase == "warm") Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"{sample} warm sample {sampleIndex} exceeded the 50ms direct service budget.");
        return outcome;
    }

    private async Task<ChildRowSavePatch<RoomTransition>> TraceTransitionAsync(string phase, int sampleIndex, string sample, Func<Task<ChildRowSavePatch<RoomTransition>>> save, SqlTraceInterceptor trace, ScopedResolverTrace resolverTrace)
    {
        trace.Reset();
        resolverTrace.Reset();
        var stopwatch = Stopwatch.StartNew();
        var patch = await save();
        stopwatch.Stop();
        var outcome = patch.Outcome;
        output.WriteLine($"ordinary text/reference {phase} trace | sample={sampleIndex} | {sample} | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | sql={trace.CommandCount} commands/{trace.Elapsed.TotalMilliseconds:F3}ms | resolver={outcome.ResolverElapsed.TotalMilliseconds:F3}ms");
        if (phase == "warm") Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"{sample} warm sample {sampleIndex} exceeded the 50ms direct service budget.");
        return patch;
    }

    private async Task<ChildRowSavePatch<SubroomConnection>> TraceConnectionAsync(string phase, int sampleIndex, string sample, Func<Task<ChildRowSavePatch<SubroomConnection>>> save, SqlTraceInterceptor trace, ScopedResolverTrace resolverTrace)
    {
        trace.Reset(); resolverTrace.Reset();
        var stopwatch = Stopwatch.StartNew();
        var patch = await save();
        stopwatch.Stop();
        output.WriteLine($"ordinary text/reference {phase} trace | sample={sampleIndex} | {sample} coordinator patch | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | sql={trace.CommandCount} commands/{trace.Elapsed.TotalMilliseconds:F3}ms | resolver={patch.Outcome.ResolverElapsed.TotalMilliseconds:F3}ms");
        if (phase == "warm") Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"{sample} warm sample {sampleIndex} exceeded the 50ms direct service budget.");
        return patch;
    }

    private async Task TraceCheckPatchAsync(string phase, int sampleIndex, string sample, Func<Task<CheckSavePatch>> save, SqlTraceInterceptor trace)
    {
        trace.Reset();
        var stopwatch = Stopwatch.StartNew();
        var patch = await save();
        stopwatch.Stop();
        output.WriteLine($"toggle/select/layout geometry {phase} trace | sample={sampleIndex} | {sample} | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | sql={trace.CommandCount} commands/{trace.Elapsed.TotalMilliseconds:F3}ms | resolver={patch.Outcome.ResolverElapsed.TotalMilliseconds:F3}ms");
        if (phase == "warm") Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"{sample} warm sample {sampleIndex} exceeded the 50ms direct service budget.");
    }

    private async Task TraceCheckAsync(string phase, int sampleIndex, string sample, Func<Task<ChildRowSavePatch<CheckLocation>>> save, SqlTraceInterceptor trace)
    {
        trace.Reset();
        var stopwatch = Stopwatch.StartNew();
        var patch = await save();
        stopwatch.Stop();
        output.WriteLine($"ordinary text/reference {phase} trace | sample={sampleIndex} | {sample} check patch | service={stopwatch.Elapsed.TotalMilliseconds:F3}ms | sql={trace.CommandCount} commands/{trace.Elapsed.TotalMilliseconds:F3}ms | resolver={patch.Outcome.ResolverElapsed.TotalMilliseconds:F3}ms");
        if (phase == "warm") Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"{sample} warm sample {sampleIndex} exceeded the 50ms direct service budget.");
    }

    private async Task WarmScopedQueryShapesAsync(LogicCatalogService catalog)
    {
        var roomId = await GetFirstRoomIdAsync();
        var document = await catalog.GetRoomAsync(roomId) ?? throw new InvalidOperationException("Trace fixture room was not found.");
        var note = document.Subrooms[0];
        note.Notes = "warmup text";
        await catalog.SaveWithOutcomeAsync(note);
        document.Room.ReferenceId = "room-1-warm";
        await catalog.SaveWithOutcomeAsync(document.Room);
        var subroom = document.Subrooms[0];
        subroom.ReferenceId = "subroom-1-warm";
        await catalog.SaveWithOutcomeAsync(subroom);
        document = await catalog.GetRoomAsync(roomId) ?? throw new InvalidOperationException("Trace fixture room was not found.");
        var transition = document.Transitions[0];
        var baseline = TransitionFieldDiffMapper.Clone(transition);
        transition.SourceSubroomReferenceText = "subroom-1-warm";
        transition.DestinationRoomReferenceText = "room-2-warm";
        transition = (await catalog.SaveTransitionWithPatchAsync(baseline, transition, roomId)).SavedRow!;
        baseline = TransitionFieldDiffMapper.Clone(transition);
        transition.DestinationRoomReferenceText = " room-2 ";
        transition = (await catalog.SaveTransitionWithPatchAsync(baseline, transition, roomId)).SavedRow!;
        baseline = TransitionFieldDiffMapper.Clone(transition);
        transition.DestinationTransitionAliasText = " E2 ";
        await catalog.SaveTransitionWithPatchAsync(baseline, transition, roomId);
        document = await catalog.GetRoomAsync(roomId) ?? throw new InvalidOperationException("Trace fixture room was not found.");
        transition = document.Transitions[0];
        baseline = TransitionFieldDiffMapper.Clone(transition);
        transition.Alias = "E1-warm";
        await catalog.SaveTransitionWithPatchAsync(baseline, transition, roomId);
        baseline = TransitionFieldDiffMapper.Clone(transition);
        transition.Requirements = "warmup requirements";
        await catalog.SaveTransitionWithPatchAsync(baseline, transition, roomId);
        document = await catalog.GetRoomAsync(roomId) ?? throw new InvalidOperationException("Trace fixture room was not found.");
        var connection = document.Connections[0];
        var connectionSaves = new ConnectionRoomSaveCoordinator(catalog);
        connectionSaves.RegisterRows(document.Connections);
        connection.SourceSubroomReferenceText = "subroom-1-warm";
        await connectionSaves.SaveAsync(connection, roomId);
        connection.DestinationSubroomReferenceText = "subroom-1-warm";
        await connectionSaves.SaveAsync(connection, roomId);
        connection.Requirements = "warmup requirements";
        await connectionSaves.SaveAsync(connection, roomId);
        document = await catalog.GetRoomAsync(roomId) ?? throw new InvalidOperationException("Trace fixture room was not found.");
        var check = document.Checks[0];
        check.SubroomReferenceText = "subroom-1-warm";
        await catalog.SaveWithOutcomeAsync(check);
        var checkBaseline = new CheckFieldDiffMapper().Clone(check);
        check.Requirements = "warmup requirements";
        await catalog.SaveCheckWithPatchAsync(checkBaseline, check, roomId);
        var groupId = (await catalog.GetWorkspaceAsync()).RoomGroups[0].Id;
        var group = (await catalog.GetGroupEditorDataAsync(groupId))!.Group;
        group.ZoneReferenceText = "zone-warm";
        await catalog.SaveWithOutcomeAsync(group);
    }

    private async Task<RoomDocument> GetFirstRoomDocumentAsync(LogicCatalogService catalog)
        => await catalog.GetRoomAsync(await GetFirstRoomIdAsync()) ?? throw new InvalidOperationException("Trace fixture room was not found.");

    private static string TrimmedReferenceForSample(string value, int sampleIndex) => sampleIndex % 2 == 0 ? $" {value} " : value;

    private async Task<Guid> GetFirstRoomIdAsync()
    {
        await using var db = CreateContext();
        return await db.Rooms.OrderBy(item => item.SortOrder).Select(item => item.Id).FirstAsync();
    }

    private static async Task SeedFixtureAsync(LogicDbContext db)
    {
        var groups = Enumerable.Range(0, 4).Select(index => new RoomGroup { FriendlyName = $"Group {index + 1}", SortOrder = index }).ToArray();
        db.AddRange(groups);
        await db.SaveChangesAsync();
        var map = new Map { InGameId = "map", SortOrder = 0 };
        db.Maps.Add(map);
        await db.SaveChangesAsync();
        db.MapZones.Add(new MapZone { MapId = map.Id, InGameId = "zone" });
        await db.SaveChangesAsync();
        var rooms = Enumerable.Range(0, 58).Select(index => new Room { RoomGroupId = groups[index % groups.Length].Id, FriendlyName = $"Room {index + 1}", ReferenceId = $"room-{index + 1}", SortOrder = index }).ToArray();
        db.AddRange(rooms);
        await db.SaveChangesAsync();

        db.AddRange(Enumerable.Range(0, 78).Select(index => new Subroom { RoomId = rooms[index % rooms.Length].Id, FriendlyName = $"Subroom {index + 1}", ReferenceId = $"subroom-{index + 1}", SortOrder = index / rooms.Length }));
        db.AddRange(Enumerable.Range(0, 159).Select(index => new RoomTransition { RoomId = rooms[index % rooms.Length].Id, FriendlyName = $"Transition {index + 1}", Alias = $"E{index + 1}", SourceSubroomReferenceText = index == 0 ? "subroom-1" : null, SortOrder = index / rooms.Length }));
        db.AddRange(Enumerable.Range(0, 109).Select(index => new SubroomConnection { RoomId = rooms[index % rooms.Length].Id, FriendlyName = $"Connection {index + 1}", Alias = $"C{index + 1}", SourceSubroomReferenceText = index == 0 ? "subroom-1" : string.Empty, DestinationSubroomReferenceText = index == 0 ? "subroom-1" : string.Empty, SortOrder = index / rooms.Length }));
        db.AddRange(Enumerable.Range(0, 139).Select(index => new CheckLocation { RoomId = rooms[index % rooms.Length].Id, FriendlyName = $"Check {index + 1}", SubroomReferenceText = index == 0 ? "subroom-1" : null, SortOrder = index / rooms.Length }));
        await db.SaveChangesAsync();
    }

    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);

    private sealed class TraceDbContextFactory(string path, SqlTraceInterceptor trace) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").AddInterceptors(trace).Options);
    }

    private sealed class SqlTraceInterceptor : DbCommandInterceptor
    {
        private readonly Dictionary<DbCommand, Stopwatch> timers = [];
        public int CommandCount { get; private set; }
        public TimeSpan Elapsed { get; private set; }
        public void Reset() { timers.Clear(); CommandCount = 0; Elapsed = TimeSpan.Zero; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Start(command); return ValueTask.FromResult(result); }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) { Stop(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Start(command); return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) { Stop(command); return ValueTask.FromResult(result); }
        private void Start(DbCommand command) { CommandCount++; timers[command] = Stopwatch.StartNew(); }
        private void Stop(DbCommand command) { if (timers.Remove(command, out var timer)) Elapsed += timer.Elapsed; }
    }

    private sealed record TracePhaseOutcome(CatalogSaveOutcome Outcome, Guid TransitionId)
    {
        public bool Changed => Outcome.Changed;
        public bool Resolved => Outcome.Resolved;
        public bool RequiresDocumentRefresh => Outcome.RequiresDocumentRefresh;
    }
}
