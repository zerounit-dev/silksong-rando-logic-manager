using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Silksong_Rando_Logic_Manager.Services.HttpLogging;

// Owned exclusively by the hosted writer. No domain context, migration, ATTACH,
// account lookup, repair, retention, or retry of an uncertain envelope.
internal sealed class HttpLogStore : IDisposable
{
    private readonly SqliteConnection connection;

    private const string Requests = """
        CREATE TABLE HttpRequests (
            Id INTEGER PRIMARY KEY,
            RequestId TEXT NOT NULL,
            StartedUtc TEXT NOT NULL,
            FinishedUtc TEXT NOT NULL,
            Method TEXT NOT NULL,
            Path TEXT NOT NULL,
            QueryString TEXT NOT NULL,
            AuthenticationState TEXT NOT NULL CHECK
                (AuthenticationState IN ('unobserved','anonymous','account','bypass')),
            AccountId TEXT NULL,
            ObservedStatusCode INTEGER NULL CHECK
                (ObservedStatusCode IS NULL OR ObservedStatusCode BETWEEN 100 AND 599),
            Outcome TEXT NOT NULL CHECK (Outcome IN ('completed','incomplete')),
            TotalElapsedMs REAL NOT NULL CHECK (TotalElapsedMs >= 0),
            ResponseStartElapsedMs REAL NULL CHECK
                (ResponseStartElapsedMs IS NULL OR
                 ResponseStartElapsedMs BETWEEN 0 AND TotalElapsedMs),
            ResponseCompletionElapsedMs REAL NULL CHECK
                (ResponseCompletionElapsedMs IS NULL OR ResponseCompletionElapsedMs >= 0),
            CHECK ((AuthenticationState = 'account' AND AccountId IS NOT NULL) OR
                   (AuthenticationState <> 'account' AND AccountId IS NULL)),
            CHECK (ResponseCompletionElapsedMs IS NULL OR
                   (Outcome = 'completed' AND ResponseStartElapsedMs IS NOT NULL)),
            CHECK (Outcome <> 'completed' OR ObservedStatusCode IS NOT NULL)
        )
        """;
    private const string Exceptions = """
        CREATE TABLE HttpExceptions (
            Id INTEGER PRIMARY KEY,
            HttpRequestId INTEGER NOT NULL REFERENCES HttpRequests(Id),
            OccurredUtc TEXT NOT NULL,
            Ordinal INTEGER NOT NULL CHECK (Ordinal >= 1),
            ExceptionType TEXT NOT NULL,
            Message TEXT NOT NULL,
            ExceptionText TEXT NOT NULL,
            UNIQUE (HttpRequestId, Ordinal)
        )
        """;
    private static readonly (string Type, string Name, string Table, string? Sql)[] Schema =
    [
        ("table", "HttpRequests", "HttpRequests", Requests),
        ("index", "IX_HttpRequests_StartedUtc", "HttpRequests", "CREATE INDEX IX_HttpRequests_StartedUtc ON HttpRequests(StartedUtc)"),
        ("index", "IX_HttpRequests_RequestId", "HttpRequests", "CREATE INDEX IX_HttpRequests_RequestId ON HttpRequests(RequestId)"),
        ("table", "HttpExceptions", "HttpExceptions", Exceptions),
        ("index", "sqlite_autoindex_HttpExceptions_1", "HttpExceptions", null)
    ];

    internal HttpLogStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            ForeignKeys = true, DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            if (!string.Equals(Scalar("PRAGMA quick_check;") as string, "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("Logging store integrity unavailable.");
            var journal = Scalar("PRAGMA journal_mode=DELETE;")?.ToString();
            if (!string.Equals(journal, "delete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Logging journal mode unavailable.");
            Execute("PRAGMA synchronous=FULL;");
            if (Convert.ToInt64(Scalar("PRAGMA foreign_keys;"), CultureInfo.InvariantCulture) != 1 ||
                Convert.ToInt64(Scalar("PRAGMA synchronous;"), CultureInfo.InvariantCulture) != 2)
                throw new InvalidOperationException("Logging connection policy unavailable.");
            // Native journal recovery is left to SQLite. A bad schema is never
            // overwritten, adopted, renamed, cleared, or migrated.
            using (var transaction = connection.BeginTransaction())
            {
                var actual = ReadSchema(transaction);
                if (actual.Count == 0)
                    foreach (var item in Schema.Where(item => item.Sql is not null))
                        Execute(item.Sql!, transaction);
                ValidateSchema(transaction);
                transaction.Commit();
            }
        }
        catch { connection.Dispose(); throw; }
    }

    private List<(string Type, string Name, string Table, string? Sql)> ReadSchema(SqliteTransaction transaction)
    {
        using var command = Command("SELECT type,name,tbl_name,sql FROM sqlite_schema;", transaction);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, string?)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private void ValidateSchema(SqliteTransaction transaction)
    {
        var actual = ReadSchema(transaction);
        // Exact owned DDL validates columns, PK/FK, CHECK and UNIQUE constraints,
        // and index definitions together, rather than merely finding table names.
        // Case/whitespace runs outside string literals are insignificant; literal
        // values, identifiers, extra objects, defaults and constraints are not.
        if (actual.Count != Schema.Length || Schema.Any(expected => !actual.Any(row =>
            row.Type == expected.Type && row.Name == expected.Name && row.Table == expected.Table &&
            Normalize(row.Sql) == Normalize(expected.Sql))))
            throw new InvalidOperationException("Incompatible logging schema.");
        using var command = Command("PRAGMA foreign_key_check;", transaction);
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException("Logging foreign key integrity unavailable.");
    }

    private static string? Normalize(string? sql)
    {
        if (sql is null) return null;
        var result = new StringBuilder();
        bool literal = false;
        bool space = false;
        foreach (var character in sql)
        {
            if (!literal && char.IsWhiteSpace(character)) { space = true; continue; }
            if (space && result.Length > 0) result.Append(' ');
            space = false;
            if (character == '\'') literal = !literal;
            result.Append(literal ? character : char.ToUpperInvariant(character));
        }
        return result.ToString().TrimEnd(';');
    }

    internal void Insert(HttpLogEnvelope envelope)
    {
        using var transaction = connection.BeginTransaction();
        using var request = Command("""
            INSERT INTO HttpRequests
            (RequestId,StartedUtc,FinishedUtc,Method,Path,QueryString,AuthenticationState,AccountId,
             ObservedStatusCode,Outcome,TotalElapsedMs,ResponseStartElapsedMs,ResponseCompletionElapsedMs)
            VALUES ($request,$started,$finished,$method,$path,$query,$auth,$account,$status,$outcome,$total,$start,$completion);
            SELECT last_insert_rowid();
            """, transaction);
        Add(request, "$request", envelope.RequestId); Add(request, "$started", Utc(envelope.StartedUtc));
        Add(request, "$finished", Utc(envelope.FinishedUtc)); Add(request, "$method", envelope.Method);
        Add(request, "$path", envelope.Path); Add(request, "$query", envelope.QueryString);
        Add(request, "$auth", envelope.AuthenticationState); Add(request, "$account", envelope.AccountId);
        Add(request, "$status", envelope.ObservedStatusCode); Add(request, "$outcome", envelope.Outcome);
        Add(request, "$total", envelope.TotalElapsedMs); Add(request, "$start", envelope.ResponseStartElapsedMs);
        Add(request, "$completion", envelope.ResponseCompletionElapsedMs);
        var id = (long)request.ExecuteScalar()!;
        foreach (var error in envelope.Exceptions)
        {
            using var exception = Command("""
                INSERT INTO HttpExceptions
                (HttpRequestId,OccurredUtc,Ordinal,ExceptionType,Message,ExceptionText)
                VALUES ($request,$occurred,$ordinal,$type,$message,$text);
                """, transaction);
            Add(exception, "$request", id); Add(exception, "$occurred", Utc(error.OccurredUtc));
            Add(exception, "$ordinal", error.Ordinal); Add(exception, "$type", error.ExceptionType);
            Add(exception, "$message", error.Message); Add(exception, "$text", error.ExceptionText);
            exception.ExecuteNonQuery();
        }
        transaction.Commit(); // Disposal rolls back any uncommitted partial envelope.
    }

    private static string Utc(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null) => new(sql, connection, transaction) { CommandTimeout = 1 };
    private void Execute(string sql, SqliteTransaction? transaction = null) { using var command = Command(sql, transaction); command.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
    public void Dispose() => connection.Dispose();
}
