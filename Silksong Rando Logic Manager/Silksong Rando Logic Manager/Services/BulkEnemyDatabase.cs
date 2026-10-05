using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

// Operational BI1 infrastructure, not a persisted snapshot or domain exchange format.
internal static class BulkEnemyDatabase
{
    internal static SqliteConnection Connection(string path, bool readOnly) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        Pooling = false, ForeignKeys = true
    }.ToString());

    internal static LogicDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite(connection).Options);

    // Encoding v1: BinaryWriter little-endian primitives/length-prefixed UTF8 strings;
    // typed cells: 0=null, 1=int64, 2=IEEE754 double bits, 3=text, 4=length+blob.
    // Schema excludes physical rootpage; tables and encoded rows sort ordinal/bytewise.
    internal static string Fingerprint(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(Action<BinaryWriter> write)
        {
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) write(writer);
            hash.AppendData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        }
        Append(w => w.Write("BI1-logical-sqlite-v1"));
        foreach (var pragma in new[] { "user_version", "application_id", "encoding" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA {pragma}";
            Append(w => { w.Write(pragma); Cell(w, command.ExecuteScalar()); });
        }
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type COLLATE BINARY, name COLLATE BINARY, tbl_name COLLATE BINARY";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                Append(w => { w.Write("schema"); for (var i = 0; i < 4; i++) Cell(w, reader.GetValue(i)); });
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        }
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
            using var reader = command.ExecuteReader();
            Append(w => { w.Write("table"); w.Write(table); w.Write(reader.FieldCount); for (var i = 0; i < reader.FieldCount; i++) w.Write(reader.GetName(i)); });
            var rows = new List<byte[]>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                    for (var i = 0; i < reader.FieldCount; i++) Cell(writer, reader.GetValue(i));
                rows.Add(bytes.ToArray());
            }
            rows.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
            Append(w => w.Write(rows.Count));
            foreach (var row in rows) Append(w => { w.Write(row.Length); w.Write(row); });
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Cell(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull: writer.Write((byte)0); break;
            case long number: writer.Write((byte)1); writer.Write(number); break;
            case double number: writer.Write((byte)2); writer.Write(BitConverter.DoubleToInt64Bits(number)); break;
            case string text: writer.Write((byte)3); writer.Write(text); break;
            case byte[] bytes: writer.Write((byte)4); writer.Write(bytes.Length); writer.Write(bytes); break;
            default: throw new InvalidOperationException($"Unsupported SQLite storage type {value.GetType().Name}.");
        }
    }
}

internal sealed class BulkEnemyContextFactory(string databasePath) : IDbContextFactory<LogicDbContext>
{
    public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true
        }.ToString()).Options);
}
