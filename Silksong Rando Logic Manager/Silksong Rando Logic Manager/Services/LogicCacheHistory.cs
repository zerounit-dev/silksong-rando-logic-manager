using Microsoft.Data.Sqlite;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Process-lifetime identity log, accessed ONLY under publication admission.
/// No row bodies, timers, durable history, counter or synthetic clock.</summary>
internal sealed class LogicCacheHistory
{
    private sealed record Entry(DateTime ChangedUtc, bool Deleted);
    private readonly Dictionary<(LogicCacheTable Table, string Key), Entry> latest = [];
    private LogicCacheCheckpoint checkpoint;
    internal LogicCacheSql.SchemaManifest Schema { get; }
    internal AppliedCompletionIndex Completion { get; }
    internal LogicCacheCheckpoint Checkpoint => checkpoint;

    internal LogicCacheHistory(LogicCacheSql.SchemaManifest schema, AppliedCompletionIndex completion)
    {
        Schema = schema;
        Completion = completion;
        var started = DateTime.UtcNow;
        checkpoint = new(1, Guid.NewGuid(), started, started, LogicCacheSql.Fingerprint(schema));
    }

    internal void PublishCommitted(IReadOnlyList<LogicChangeCapture.Change> changes,
        AppliedCompletionIndex.Pending completion)
    {
        completion.PublishCommitted();
        if (changes.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var change in changes)
            latest[(change.Table, change.Key)] = new(now, change.After is null);
        checkpoint = checkpoint with { ThroughUtc = now };
    }

    internal async Task<LogicCacheChanges> ReadAsync(SqliteConnection connection,
        SqliteTransaction transaction, LogicCacheCheckpoint? known, CancellationToken token)
    {
        var through = checkpoint;
        if (known is null || known.Format != through.Format || known.RunId != through.RunId
            || known.Schema != through.Schema || known.StartedUtc != through.StartedUtc
            || known.ThroughUtc < through.StartedUtc || known.ThroughUtc > through.ThroughUtc)
            return new(known, through, true, []);
        var patches = new List<LogicCacheTablePatch>();
        // Inclusive UTC boundary deliberately repeats same-time identities. Read
        // actual current complete rows under the same admission as the log snapshot.
        foreach (var group in latest.Where(x => x.Value.ChangedUtc >= known.ThroughUtc)
            .GroupBy(x => x.Key.Table).OrderBy(x => x.Key))
        {
            var columns = Schema.Tables.Single(x => x.Name == LogicCacheSql.Name(group.Key)).Columns;
            patches.Add(await LogicCacheSql.ReadPatchAsync(connection, transaction, group.Key, columns,
                group.Select(x => x.Key.Key).Order(StringComparer.Ordinal).ToArray(), token));
        }
        return new(known, through, false, patches.AsReadOnly());
    }
}
