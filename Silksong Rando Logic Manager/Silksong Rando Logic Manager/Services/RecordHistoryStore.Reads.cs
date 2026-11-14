using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record HistorySelector(Guid EpochId, string Ordinal, string TableName, string RecordKey)
{
    internal bool SameRecordAs(HistorySelector other) =>
        string.Equals(TableName, other.TableName, StringComparison.Ordinal)
        && string.Equals(RecordKey, other.RecordKey, StringComparison.Ordinal);
}
internal sealed record HistoryQuery(string Capability, Guid? EpochId = null, string? TableName = null,
    string? RecordKey = null, long? After = null, int Limit = 100, HistorySelector? First = null, HistorySelector? Second = null);

internal sealed partial class RecordHistoryStore
{
    internal sealed record Operation(string Ordinal, DateTime RecordedUtc, Guid ActorId, string Kind);
    internal sealed record EpochDescriptor(string Cursor, Guid EpochId, DateTime StartedUtc,
        IReadOnlyList<LogicCacheSql.TableSchema> Schema, bool BaselineAvailable, bool CurrentRun);
    internal sealed record Coverage(bool BaselineAvailable, string? FirstMissingOrdinal, string? HeadAtRead,
        string? LastStoredOrdinal, bool HeadIsAttemptedWatermark);
    internal sealed record StoredRevision(Operation Operation, string TableName, string RecordKey,
        bool IsDeleted, LogicCacheRow? Before, LogicCacheRow? After);
    internal sealed record Point(string Status, HistorySelector Selector,
        IReadOnlyList<string>? Columns = null, LogicCacheRow? Row = null, StoredRevision? Evidence = null, Coverage? Coverage = null);
    internal sealed record ColumnValue(bool Missing, LogicCacheCell? Cell);
    internal sealed record ColumnDifference(string Column, ColumnValue Before, ColumnValue After);
    private sealed record EpochState(EpochDescriptor Descriptor, Coverage Coverage, long? Head, long? FirstMissing);

    // The caller holds publication admission and has freshly revalidated Normal.
    // Both diff selectors share this snapshot AND this one sampled process watermark.
    internal async Task<object> ReadAsync(HistoryQuery query, CancellationToken token)
    {
        if (query.Capability == "diff"
            && (query.First is null || query.Second is null || !query.First.SameRecordAs(query.Second)))
            throw new ArgumentException("History diff requires the same exact table and record key.", nameof(query));
        var currentEpoch = epoch;
        var watermark = attempted;
        try
        {
            token.ThrowIfCancellationRequested();
            await using var connection = Connection(false);
            await connection.OpenAsync(token);
            await using var transaction = connection.BeginTransaction(deferred: true);
            await CheckFormatAsync(connection, transaction);
            if (query.Capability == "epochs")
            {
                var epochs = new List<EpochDescriptor>();
                await using var command = Command(connection, transaction,
                    "SELECT rowid,EpochId,StartedUtc,SchemaJson FROM Epochs WHERE rowid>$after ORDER BY rowid LIMIT $limit",
                    ("$after", query.After ?? -1), ("$limit", query.Limit + 1));
                // Close reader before per-epoch metadata reads.
                var raw = new List<(long Cursor, string Id, string Utc, string Schema)>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) raw.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                var more = raw.Count > query.Limit;
                foreach (var row in raw.Take(query.Limit))
                {
                    var id = Guid.Parse(row.Id);
                    var coverage = await ReadCoverageAsync(connection, transaction, row.Id, id == currentEpoch ? watermark : null);
                    epochs.Add(new(Number(row.Cursor), id, Utc(row.Utc), DecodeSchema(row.Schema), coverage.BaselineAvailable, id == currentEpoch));
                }
                var current = await ReadEpochAsync(connection, transaction, currentEpoch, currentEpoch, watermark);
                return new { Status = "Available", Epochs = epochs,
                    CurrentRun = new { EpochId = currentEpoch, StartedUtc = startedUtc, Schema = schema,
                        AttemptedWatermark = Number(watermark), Persisted = current is not null, Coverage = current?.Coverage },
                    HasMore = more, EndOfSnapshot = !more,
                    NextCursor = epochs.LastOrDefault()?.Cursor ?? (query.After is { } a ? Number(a) : null) };
            }
            if (query.Capability == "diff")
            {
                var before = await ReadPointAsync(connection, transaction, query.First!, currentEpoch, watermark);
                var after = await ReadPointAsync(connection, transaction, query.Second!, currentEpoch, watermark);
                var known = Known(before) && Known(after);
                var differences = new List<ColumnDifference>();
                if (known)
                {
                    foreach (var column in (before.Columns ?? []).Concat(after.Columns ?? []).Distinct(StringComparer.Ordinal))
                    {
                        var left = Value(before, column);
                        var right = Value(after, column);
                        if (left != right) differences.Add(new(column, left, right));
                    }
                }
                return new { Status = known ? "Known" : "Unknown", Before = before, After = after,
                    Differences = differences, CrossEpoch = query.First!.EpochId != query.Second!.EpochId };
            }
            if (query.Capability == "point") return await ReadPointAsync(connection, transaction, query.First!, currentEpoch, watermark);
            var state = await ReadEpochAsync(connection, transaction, query.EpochId!.Value, currentEpoch, watermark);
            if (state is null) return new { Status = "UnknownEpoch", EpochId = query.EpochId };
            if (query.Capability == "boundaries")
                return await BoundariesAsync(connection, transaction, state, query, token);
            if (!state.Descriptor.Schema.Any(x => x.Name == query.TableName)) return new { Status = "UnknownTable" };
            var revisions = await RevisionsAsync(connection, transaction, state, query.TableName!, query.RecordKey!,
                query.After, query.Limit + 1, null, token);
            var hasMore = revisions.Count > query.Limit;
            var page = revisions.Take(query.Limit).ToArray();
            return new { Status = "Available", Epoch = state.Descriptor, state.Coverage, Revisions = page,
                HasMore = hasMore, EndOfSnapshot = !hasMore,
                NextAfterOrdinal = page.LastOrDefault()?.Operation.Ordinal ?? (query.After is { } cursor ? Number(cursor) : null) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            LogFailure(exception, "read");
            return new { Status = "HistoryUnavailable", CurrentRun = new { EpochId = currentEpoch, StartedUtc = startedUtc,
                Schema = schema, AttemptedWatermark = Number(watermark) } };
        }
    }

    private static bool Known(Point point) => point.Status is "Present" or "Deleted" or "Absent";
    private static ColumnValue Value(Point point, string column)
    {
        var index = point.Columns?.ToList().FindIndex(x => x == column) ?? -1;
        return index < 0 || point.Row is null ? new(true, null) : new(false, point.Row.Values[index]);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static DateTime Utc(string text)
    {
        if (!DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            || value.Kind != DateTimeKind.Utc) throw new InvalidDataException("Invalid history UTC metadata.");
        return value;
    }

    private static IReadOnlyList<LogicCacheSql.TableSchema> DecodeSchema(string text)
    {
        var tables = JsonSerializer.Deserialize<LogicCacheSql.TableSchema[]>(text, StoredJson)
            ?? throw new InvalidDataException("Missing history schema.");
        if (tables.Length != 17 || tables.Any(x => string.IsNullOrEmpty(x.Name) || x.Columns is null || x.Columns.Count == 0)
            || tables.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != tables.Length)
            throw new InvalidDataException("Invalid historical table inventory.");
        foreach (var table in tables)
            if (!table.Columns.Select(x => x.Ordinal).SequenceEqual(Enumerable.Range(0, table.Columns.Count))
                || table.Columns.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != table.Columns.Count
                || table.Columns.Count(x => x.PrimaryKeyOrdinal != 0) != 1
                || !table.Columns.Any(x => x.Name == "Id" && x.PrimaryKeyOrdinal == 1 && x.DeclaredType == "TEXT"))
                throw new InvalidDataException("Invalid historical columns.");
        return Array.AsReadOnly(tables);
    }

    private static async Task<EpochState?> ReadEpochAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, Guid currentEpoch, long watermark)
    {
        var key = id.ToString("D");
        await using var command = Command(connection, transaction,
            "SELECT rowid,StartedUtc,SchemaJson FROM Epochs WHERE EpochId=$epoch", ("$epoch", key));
        EpochDescriptor descriptor;
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            if (!await reader.ReadAsync(CancellationToken.None)) return null;
            descriptor = new(Number(reader.GetInt64(0)), id, Utc(reader.GetString(1)), DecodeSchema(reader.GetString(2)), false, id == currentEpoch);
        }
        var coverage = await ReadCoverageAsync(connection, transaction, key, id == currentEpoch ? watermark : null);
        return new(descriptor with { BaselineAvailable = coverage.BaselineAvailable }, coverage,
            coverage.HeadAtRead is { } h ? long.Parse(h, CultureInfo.InvariantCulture) : null,
            coverage.FirstMissingOrdinal is { } m ? long.Parse(m, CultureInfo.InvariantCulture) : null);
    }

    private static async Task<Coverage> ReadCoverageAsync(SqliteConnection connection, SqliteTransaction transaction, string epochId, long? watermark)
    {
        await using var command = Command(connection, transaction,
            "SELECT MAX(Ordinal),EXISTS(SELECT 1 FROM Operations WHERE EpochId=$epoch AND Ordinal=0 AND Kind='Baseline') FROM Operations WHERE EpochId=$epoch",
            ("$epoch", epochId));
        long? last;
        bool baseline;
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        { await reader.ReadAsync(CancellationToken.None); last = reader.IsDBNull(0) ? null : reader.GetInt64(0); baseline = reader.GetBoolean(1); }
        var head = watermark ?? last;
        if (watermark is { } w && last > w) throw new InvalidDataException("History exceeds current attempted watermark.");
        long? missing = baseline ? null : 0;
        if (baseline && head is { } cutoff)
        {
            await using var gap = Command(connection, transaction, """
                SELECT MIN(a.Ordinal+1) FROM Operations a
                WHERE a.EpochId=$epoch AND a.Ordinal<$head
                AND NOT EXISTS(SELECT 1 FROM Operations b WHERE b.EpochId=a.EpochId AND b.Ordinal=a.Ordinal+1)
                """, ("$epoch", epochId), ("$head", cutoff));
            var raw = await gap.ExecuteScalarAsync(CancellationToken.None);
            if (raw is not null && raw is not DBNull) missing = (long)raw;
        }
        return new(baseline, missing is { } m ? Number(m) : null, head is { } h ? Number(h) : null,
            last is { } l ? Number(l) : null, watermark is not null);
    }

    private static Operation ReadOperation(SqliteDataReader reader, int offset = 0)
    {
        var ordinal = reader.GetInt64(offset);
        var kind = reader.GetString(offset + 3);
        if (ordinal < 0 || (ordinal == 0 ? kind != "Baseline" : kind != "Change")) throw new InvalidDataException("Invalid history operation.");
        var actor = Guid.Parse(reader.GetString(offset + 2));
        if (ordinal == 0 && actor != Guid.Empty) throw new InvalidDataException("Invalid baseline actor.");
        return new(Number(ordinal), Utc(reader.GetString(offset + 1)), actor, kind);
    }

    private static async Task<object> BoundariesAsync(SqliteConnection connection, SqliteTransaction transaction,
        EpochState state, HistoryQuery query, CancellationToken token)
    {
        var operations = new List<Operation>();
        await using var command = Command(connection, transaction,
            "SELECT Ordinal,RecordedUtc,ActorId,Kind FROM Operations WHERE EpochId=$epoch AND Ordinal>$after ORDER BY Ordinal LIMIT $limit",
            ("$epoch", state.Descriptor.EpochId.ToString("D")), ("$after", query.After ?? -1), ("$limit", query.Limit + 1));
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) operations.Add(ReadOperation(reader));
        var more = operations.Count > query.Limit;
        var page = operations.Take(query.Limit).ToArray();
        long? from = null, through = null;
        var gaps = new List<object>();
        if (state.Head is { } head && (query.After is null || query.After < head))
        {
            from = query.After is { } after ? after + 1 : 0;
            through = more ? long.Parse(page[^1].Ordinal, CultureInfo.InvariantCulture) : head;
            var next = from.Value;
            foreach (var operation in page)
            {
                var ordinal = long.Parse(operation.Ordinal, CultureInfo.InvariantCulture);
                if (next < ordinal) gaps.Add(new { From = Number(next), Through = Number(ordinal - 1) });
                if (ordinal == long.MaxValue) { next = ordinal; break; }
                next = ordinal + 1;
            }
            if (next <= through && (page.Length == 0 || page[^1].Ordinal != Number(through.Value)))
                gaps.Add(new { From = Number(next), Through = Number(through.Value) });
        }
        return new { Status = "Available", Epoch = state.Descriptor, state.Coverage,
            Operations = page, MissingRanges = gaps, ExaminedFrom = from is { } f ? Number(f) : null,
            ExaminedThrough = through is { } t ? Number(t) : null, state.Coverage.HeadAtRead,
            NextAfterOrdinal = through is { } n ? Number(n) : query.After is { } a ? Number(a) : null,
            HasMore = more, EndOfSnapshot = !more };
    }

    private static async Task<List<StoredRevision>> RevisionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        EpochState state, string table, string key, long? after, int limit, long? cutoff, CancellationToken token)
    {
        await using var command = Command(connection, transaction, """
            SELECT o.Ordinal,o.RecordedUtc,o.ActorId,o.Kind,r.IsDeleted,r.BeforeJson,r.AfterJson
            FROM Revisions r JOIN Operations o ON o.EpochId=r.EpochId AND o.Ordinal=r.Ordinal
            WHERE r.EpochId=$epoch AND r.TableName=$table AND r.RecordKey=$key AND r.Ordinal>$after
            AND ($cutoff IS NULL OR r.Ordinal<=$cutoff)
            """ + (cutoff is null ? " ORDER BY r.Ordinal LIMIT $limit" : " ORDER BY r.Ordinal DESC LIMIT $limit"),
            ("$epoch", state.Descriptor.EpochId.ToString("D")), ("$table", table), ("$key", key),
            ("$after", after ?? -1), ("$cutoff", cutoff), ("$limit", limit));
        var rows = new List<StoredRevision>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var operation = ReadOperation(reader);
            LogicCacheRow? Decode(int index) => reader.IsDBNull(index) ? null
                : JsonSerializer.Deserialize<LogicCacheRow>(reader.GetString(index), StoredJson)
                    ?? throw new InvalidDataException("Invalid history row JSON.");
            var revision = new Revision(table, key, reader.GetInt64(4) switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid tombstone.") }, Decode(5), Decode(6));
            ValidateRevision(state.Descriptor.Schema, revision);
            if (operation.Kind == "Baseline" && (revision.Before is not null || revision.After is null)) throw new InvalidDataException("Invalid baseline revision.");
            rows.Add(new(operation, table, key, revision.IsDeleted, revision.Before, revision.After));
        }
        return rows;
    }

    private static async Task<Point> ReadPointAsync(SqliteConnection connection, SqliteTransaction transaction,
        HistorySelector selector, Guid currentEpoch, long watermark)
    {
        var state = await ReadEpochAsync(connection, transaction, selector.EpochId, currentEpoch, watermark);
        if (state is null) return new("UnknownEpoch", selector);
        var table = state.Descriptor.Schema.SingleOrDefault(x => x.Name == selector.TableName);
        if (table is null) return new("UnknownTable", selector, Coverage: state.Coverage);
        var cutoff = long.Parse(selector.Ordinal, CultureInfo.InvariantCulture);
        await using var command = Command(connection, transaction,
            "SELECT Ordinal,RecordedUtc,ActorId,Kind FROM Operations WHERE EpochId=$epoch AND Ordinal=$ordinal",
            ("$epoch", selector.EpochId.ToString("D")), ("$ordinal", cutoff));
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            if (!await reader.ReadAsync(CancellationToken.None)) return new("UnknownBoundary", selector, Coverage: state.Coverage);
            _ = ReadOperation(reader);
        }
        if (!state.Coverage.BaselineAvailable || state.FirstMissing is { } missing && missing <= cutoff)
            return new("UnknownCoverage", selector, Coverage: state.Coverage);
        var revisions = await RevisionsAsync(connection, transaction, state, selector.TableName, selector.RecordKey,
            null, 1, cutoff, CancellationToken.None);
        var columns = table.Columns.Select(x => x.Name).ToArray();
        if (revisions.Count == 0) return new("Absent", selector, columns, Coverage: state.Coverage);
        var revision = revisions[0];
        return revision.IsDeleted ? new("Deleted", selector, columns, Evidence: revision, Coverage: state.Coverage)
            : new("Present", selector, columns, revision.After, revision, state.Coverage);
    }
}
