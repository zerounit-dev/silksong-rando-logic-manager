using System.Diagnostics;
using System.Globalization;
using SilksongRandoLogicManager.Contracts.Diagnostics;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Invocation-local numeric observations only. Never retained by a data/cache owner.
internal sealed class LocalLoadWork
{
    private long rows, yields;
    private long rooms, groups, transitions, connections, checks, vocabulary, maps, zones, scenes, chunks, overlays;
    private double yieldMilliseconds;
    internal void Row(LogicCacheTable table)
    {
        rows++;
        switch (table)
        {
            case LogicCacheTable.Rooms: rooms++; break;
            case LogicCacheTable.RoomGroups: groups++; break;
            case LogicCacheTable.RoomTransitions: transitions++; break;
            case LogicCacheTable.SubroomConnections: connections++; break;
            case LogicCacheTable.CheckLocations: checks++; break;
            case LogicCacheTable.RequirementPredicates:
            case LogicCacheTable.RequirementCrests:
            case LogicCacheTable.RequirementMethods:
            case LogicCacheTable.RequirementItems:
            case LogicCacheTable.RequirementCountables:
            case LogicCacheTable.RequirementSouls: vocabulary++; break;
            case LogicCacheTable.Maps: maps++; break;
            case LogicCacheTable.MapZones: zones++; break;
            case LogicCacheTable.MapScenes: scenes++; break;
            case LogicCacheTable.MapChunks: chunks++; break;
            case LogicCacheTable.MapOverlays: overlays++; break;
        }
    }
    internal YieldWait MeasureYield() => new(this);
    internal static void Request(OperationTiming stage, long requestId, long? brokerRequestId, bool success)
    {
        // Existing failure diagnostics can carry an owner ID rather than a tab ID.
        // The Tab reply log owns that exact failure mapping; never invent it here.
        try { Console.WriteLine($"[v3-local-load] span={stage.Id} tab-request={(success ? Math.Max(0, requestId) : 0)} tab-mapping-available={success && requestId > 0} broker-request={Math.Max(0, brokerRequestId ?? 0)} broker-mapping-available={brokerRequestId is > 0} success={success}"); }
        catch { }
    }
    internal static void Geometry(OperationTiming stage, Silksong_Rando_Logic_Manager.Services.MapProjectionObservation counts)
    {
        try { Console.WriteLine(FormattableString.Invariant($"[v3-local-load] span={stage.Id} chunks={counts.Chunks} x-boundaries={counts.XBoundaries} y-boundaries={counts.YBoundaries} slabs={counts.Slabs} active-interval-events={counts.ActiveIntervalEvents} winning-runs={counts.WinningRuns} owners={counts.Owners} sparse-segments={counts.SparseSegments}")); }
        catch { }
    }
    internal void Report(OperationTiming stage, bool reused = false, long items = 0)
    {
        try
        {
            Console.WriteLine($"[v3-local-load] span={stage.Id} prepared-named-rows={rows} rooms={rooms} groups={groups} transitions={transitions} connections={connections} checks={checks} vocabulary={vocabulary} maps={maps} zones={zones} scenes={scenes} chunks={chunks} overlays={overlays} items={items} result-reused={reused} yields={yields} inclusive-yield-wall-ms={yieldMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        catch { }
        rows = yields = 0; yieldMilliseconds = 0;
        rooms = groups = transitions = connections = checks = vocabulary = maps = zones = scenes = chunks = overlays = 0;
    }
    internal readonly struct YieldWait : IDisposable
    {
        private readonly LocalLoadWork owner;
        private readonly long started;
        internal YieldWait(LocalLoadWork owner) { this.owner = owner; started = Stopwatch.GetTimestamp(); }
        public void Dispose() { owner.yields++; owner.yieldMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }
}
