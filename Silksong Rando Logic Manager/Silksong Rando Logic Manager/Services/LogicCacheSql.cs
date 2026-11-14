using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Exact SQLite transfer primitives. Callers hold publication admission and
/// the supplied coherent transaction. No EF value conversion, authored-date parsing,
/// row projection or client-supplied SQL participates in the wire representation.</summary>
internal static class LogicCacheSql
{
    internal sealed record Column(int Ordinal, string Name, string DeclaredType,
        bool NotNull, string? DefaultSql, int PrimaryKeyOrdinal);
    internal sealed record ForeignKey(int Id, int Sequence, string Table, string From,
        string? To, string OnUpdate, string OnDelete, string Match);
    internal sealed record TableSchema(string Name, IReadOnlyList<Column> Columns,
        IReadOnlyList<ForeignKey> ForeignKeys);
    internal sealed record Migration(string Id, string ProductVersion);
    internal sealed record SchemaManifest(IReadOnlyList<TableSchema> Tables,
        IReadOnlyList<Migration> Migrations);

    internal static readonly IReadOnlyList<LogicCacheTable> DomainTables =
        Array.AsReadOnly(Enum.GetValues<LogicCacheTable>());

    internal static string Name(LogicCacheTable table) => Enum.GetName(table)
        ?? throw new ArgumentOutOfRangeException(nameof(table));

    // Only local schema names and enum-allowlisted table names reach this function.
    // User values are always parameters, not SQL identifiers.
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    internal static LogicCacheCell Cell(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DBNull => new(LogicCacheCellType.Null, null),
        long integer => new(LogicCacheCellType.Integer, integer.ToString(CultureInfo.InvariantCulture)),
        double real => new(LogicCacheCellType.Real, real.ToString("R", CultureInfo.InvariantCulture)),
        string text => new(LogicCacheCellType.Text, text),
        byte[] blob => new(LogicCacheCellType.Blob, Convert.ToBase64String(blob)),
        _ => throw new InvalidOperationException("Unsupported SQLite storage value.")
    };

    internal static async Task<SchemaManifest> ReadSchemaAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM main.sqlite_schema WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_' ORDER BY name COLLATE BINARY";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
        }

        var tables = new List<TableSchema>();
        foreach (var name in names)
        {
            var columns = new List<Column>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT cid, name, type, \"notnull\", dflt_value, pk FROM pragma_table_info($table, 'main') ORDER BY cid";
                command.Parameters.AddWithValue("$table", name);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    columns.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
            }

            var keys = new List<ForeignKey>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, \"match\" FROM pragma_foreign_key_list($table, 'main') ORDER BY id, seq";
                command.Parameters.AddWithValue("$table", name);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    keys.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            }
            tables.Add(new(name, columns.AsReadOnly(), keys.AsReadOnly()));
        }

        // The migration history is infrastructure, included in the prepared file and
        // compatibility fingerprint but not the seventeen-table row patch allowlist.
        var migrations = new List<Migration>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT MigrationId, ProductVersion FROM main.__EFMigrationsHistory ORDER BY MigrationId COLLATE BINARY";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) migrations.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        return new(tables.AsReadOnly(), migrations.AsReadOnly());
    }

    internal static string Fingerprint(SchemaManifest schema) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(schema))));

    internal static async Task<LogicCacheTablePatch> ReadPatchAsync(SqliteConnection connection,
        SqliteTransaction transaction, LogicCacheTable table, IReadOnlyList<Column> columns,
        IReadOnlyList<string> keys, CancellationToken token)
    {
        var id = columns.Select((column, index) => (column, index))
            .Single(x => x.column.Name == "Id" && x.column.PrimaryKeyOrdinal == 1).index;
        if (columns.Count(x => x.PrimaryKeyOrdinal != 0) != 1)
            throw new InvalidOperationException("The logic cache requires the fixed single-Id domain schema.");
        var rows = new List<LogicCacheRow>();
        var deletions = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(x => Quote(x.Name)))} FROM main.{Quote(Name(table))} WHERE \"Id\" = $id";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            parameter.Value = key;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                deletions.Add(key);
                continue;
            }
            var cells = new LogicCacheCell[columns.Count];
            for (var i = 0; i < cells.Length; i++) cells[i] = Cell(reader, i);
            rows.Add(new(reader.GetString(id), Array.AsReadOnly(cells)));
        }
        return new(table, Array.AsReadOnly(columns.Select(x => x.Name).ToArray()),
            rows.AsReadOnly(), deletions.AsReadOnly());
    }
}
