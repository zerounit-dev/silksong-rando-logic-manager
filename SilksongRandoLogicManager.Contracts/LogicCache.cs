using System.Text.Json.Serialization;

namespace SilksongRandoLogicManager.Contracts;

/// <summary>The fixed domain-table allowlist. Infrastructure is transferred in the
/// prepared database, never patched as application rows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogicCacheTable>))]
public enum LogicCacheTable
{
    RoomGroups, Rooms, Subrooms, RoomTransitions, SubroomConnections, CheckLocations,
    Maps, MapZones, MapScenes, MapChunks, MapOverlays,
    RequirementPredicates, RequirementItems, RequirementCrests, RequirementMethods,
    RequirementCountables, RequirementSouls, RequirementExplanations
}

[JsonConverter(typeof(JsonStringEnumConverter<LogicCacheCellType>))]
public enum LogicCacheCellType { Null, Integer, Real, Text, Blob }

/// <summary>SQLite storage type and exact invariant wire value. INTEGER is decimal
/// text (including ticks), REAL round-trip text, BLOB base64, NULL an explicit null.
/// No numeric value is transported through a JavaScript Number.</summary>
public sealed record LogicCacheCell(LogicCacheCellType Type, string? Value);

public sealed record LogicCacheCheckpoint(int Format, Guid RunId, DateTime StartedUtc,
    DateTime ThroughUtc, string Schema);

/// <summary>Values follow Columns exactly and include every persisted column.
/// Key is the exact persisted Id text, not a re-formatted GUID.</summary>
public sealed record LogicCacheRow(string Key, IReadOnlyList<LogicCacheCell> Values);
public sealed record LogicCacheTablePatch(LogicCacheTable Table, IReadOnlyList<string> Columns,
    IReadOnlyList<LogicCacheRow> Upserts, IReadOnlyList<string> Deletions);

/// <summary>Complete database coverage, separate from the feature's result. A different
/// run/schema or missing client checkpoint requires prepared binary replacement, not
/// applying partial rows. Through describes the coherent server read, never receipt time.</summary>
public sealed record LogicCacheChanges(LogicCacheCheckpoint? From, LogicCacheCheckpoint Through,
    bool RequiresDatabase, IReadOnlyList<LogicCacheTablePatch> Tables);

/// <summary>Result keeps its original feature/receipt semantics. SQLite installation
/// of Cache is background work, not permission to incorporate or acknowledge Result.</summary>
public sealed record CacheResult<T>(T Result, LogicCacheChanges Cache);

public sealed record LogicCacheCheck(LogicCacheCheckpoint? Known);
public sealed record LogicCacheImageObservation(Guid RoomId, bool ImageExists);
public sealed record LogicCacheImageRequest(IReadOnlyList<Guid> RoomIds);

/// <summary>Initial live account/session response is never persisted in SQLite.
/// Unauthenticated/replacement-only sessions carry no protected cache or image facts.</summary>
public sealed record LogicCacheStartup(AuthenticationSession Session, LogicCacheChanges? Cache,
    IReadOnlyList<LogicCacheImageObservation> Images);
