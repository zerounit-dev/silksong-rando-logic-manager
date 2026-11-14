using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Silksong_Rando_Logic_Manager.Authentication;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Protected, publication-admitted reads of the one authoritative logic
/// database. Prepared files have request lifetime only; no private/account/image
/// bytes, domain commands, authoring replay or durable replication history.</summary>
internal sealed class LogicCacheTransfer(IDbContextFactory<LogicDbContext> contexts,
    SharedPublicationCoordinator publications, SceneImageFileService images, LiveAccess access)
{
    internal async Task<(ExecutionIdentity Identity, LogicCacheChanges Cache,
        IReadOnlyList<LogicCacheImageObservation> Images)> StartupAsync(LogicCacheCheckpoint? known, CancellationToken token)
    {
        using var lifecycle = await images.AcquireLifecycleAsync(token);
        using var publication = await publications.AcquireAsync(token);
        var identity = await access.RequireAsync(token: token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var snapshot = await db.Database.BeginTransactionAsync(token);
        var cache = await ReadAdmittedAsync(db, publication, known, token);
        var ids = await db.Rooms.AsNoTracking().Select(x => x.Id).ToArrayAsync(token);
        return (identity, cache, ObserveImages(ids));
    }

    internal async Task<LogicCacheChanges> ChangesAsync(LogicCacheCheckpoint? known, CancellationToken token)
    {
        using var publication = await publications.AcquireAsync(token);
        await access.RequireAsync(token: token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var snapshot = await db.Database.BeginTransactionAsync(token);
        return await ReadAdmittedAsync(db, publication, known, token);
    }

    // Feature responses use this after their original durable receipt/result. This
    // is complete catch-up, not targeted projection coverage or a command retry.
    internal async Task<CacheResult<T>> ResultAsync<T>(T result, LogicCacheCheckpoint? known, CancellationToken token) =>
        new(result, await ChangesAsync(known, token));

    internal static Task<LogicCacheChanges> ReadAdmittedAsync(LogicDbContext db,
        SharedPublicationCoordinator.Admission publication, LogicCacheCheckpoint? known, CancellationToken token) =>
        publication.Cache.ReadAsync((SqliteConnection)db.Database.GetDbConnection(),
            (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction(), known, token);

    internal async Task<IReadOnlyList<LogicCacheImageObservation>> ImageObservationsAsync(
        IReadOnlyList<Guid> ids, CancellationToken token)
    {
        using var lifecycle = await images.AcquireLifecycleAsync(token);
        await access.RequireAsync(token: token);
        // Deliberately no LogicDbContext, Room read or freshness/checkpoint access.
        return ObserveImages(ids);
    }

    private IReadOnlyList<LogicCacheImageObservation> ObserveImages(IEnumerable<Guid> ids) =>
        Array.AsReadOnly(ids.Select(id => new LogicCacheImageObservation(id, images.Exists(id))).ToArray());

    internal sealed class PreparedDatabase(FileStream content, LogicCacheCheckpoint checkpoint) : IAsyncDisposable
    {
        internal FileStream Content { get; } = content;
        internal LogicCacheCheckpoint Checkpoint { get; } = checkpoint;
        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }

    internal async Task<PreparedDatabase> DatabaseAsync(CancellationToken token)
    {
        // Only a disposable transfer file. The real standalone recovery backup hook
        // and all operator backup paths remain entirely unchanged (D18).
        string? temporary = null;
        try
        {
            LogicCacheCheckpoint checkpoint;
            using (var publication = await publications.AcquireAsync(token))
            {
                await access.RequireAsync(token: token);
                await using var db = await contexts.CreateDbContextAsync(token);
                await db.Database.OpenConnectionAsync(token);
                var source = (SqliteConnection)db.Database.GetDbConnection();
                temporary = Path.GetTempFileName();
                await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
                {
                    await target.OpenAsync(token);
                    token.ThrowIfCancellationRequested();
                    source.BackupDatabase(target);
                    // Validate the copied schema, not a later live read. Reject any
                    // unexpected table rather than sending a guessed/private store.
                    await using var snapshot = target.BeginTransaction(deferred: true);
                    var schema = await LogicCacheSql.ReadSchemaAsync(target, snapshot, token);
                    var domain = LogicCacheSql.DomainTables.Select(LogicCacheSql.Name).ToHashSet(StringComparer.Ordinal);
                    // EF's SQLite migration lock is infrastructure too. It can
                    // remain in the file after normal MigrateAsync; it is never a
                    // domain patch table or private account/session state.
                    if (!domain.IsSubsetOf(schema.Tables.Select(x => x.Name))
                        || !schema.Tables.Any(x => x.Name == "__EFMigrationsHistory")
                        || schema.Tables.Any(x => !domain.Contains(x.Name)
                            && x.Name is not ("__EFMigrationsHistory" or "__EFMigrationsLock"))
                        || LogicCacheSql.Fingerprint(schema) != publication.Cache.Checkpoint.Schema)
                        throw new InvalidOperationException("The prepared logic database schema does not match the initialized cache run.");
                }
                checkpoint = publication.Cache.Checkpoint;
            }
            // Admission is released BEFORE opening/streaming the immutable transfer.
            // DeleteOnClose also handles client disconnects and response-write errors.
            var content = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            temporary = null; // Ownership transferred to PreparedDatabase.
            return new(content, checkpoint);
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
        }
    }
}
