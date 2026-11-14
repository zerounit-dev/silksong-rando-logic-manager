using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

// Deliberately never registered as the live factory. Even accidental SQL writes are
// rejected by SQLite, independently of EF tracking or the consumer's transaction.
internal sealed class PublishingSnapshotFactory : IDbContextFactory<LogicDbContext>
{
    private readonly DbContextOptions<LogicDbContext> options;
    public PublishingSnapshotFactory(string path)
    {
        options = new DbContextOptionsBuilder<LogicDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path,
                Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(new DeferredSnapshotTransactions()).Options;
    }
    public LogicDbContext CreateDbContext() => new(options);
    public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }

    // EF 10 RelationalConnection.BeginTransaction[Async] honors this suppression
    // before calling DbConnection.BeginTransaction[Async], and owns/disposes the
    // supplied transaction normally. Sqlite's default uses BEGIN IMMEDIATE;
    // deferred:true uses BEGIN and is compatible with Mode=ReadOnly.
    private sealed class DeferredSnapshotTransactions : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result) =>
            InterceptionResult<DbTransaction>.SuppressWithResult(((SqliteConnection)connection).BeginTransaction(deferred: true));
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(TransactionStarting(connection, eventData, result));
        }
    }
}

internal static class PublishingSnapshot
{
    public static async Task<PublishingSnapshotFactory> AcquireAsync(string liveDatabase, string destination,
        string captures, SceneImageFileService images, SharedPublicationCoordinator publications,
        Authentication.LiveAccess access, CancellationToken cancellationToken, PublishingTimingScope timing)
    {
        // Admission covers the online backup AND copies selected from that backup,
        // not rendering/export. Never read rooms from a second live context.
        using (await timing.MeasureAsync(PublishingMetric.LifecycleWait, () => images.AcquireLifecycleAsync(cancellationToken)))
        {
            using (await timing.MeasureAsync(PublishingMetric.PublicationWait, () => publications.AcquireAsync(cancellationToken)))
            {
                await timing.MeasureAsync(PublishingMetric.Authorization, () => access.RequireAsync(Authentication.AccessPolicies.PublishDocumentation, cancellationToken));
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = liveDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
                {
                    await source.OpenAsync(cancellationToken);
                    await target.OpenAsync(cancellationToken);
                    timing.Measure(PublishingMetric.Backup, () => source.BackupDatabase(target));
                }
            }
            // Source copy is frozen. Keep image lifecycle for selected capture
            // copies, but release publication admission before projection/rendering.
            var factory = new PublishingSnapshotFactory(destination);
            await using var db = factory.CreateDbContext();
            var rooms = await timing.MeasureAsync(PublishingMetric.Load, () => db.Rooms.Where(x => !x.IsArchived && x.UseSceneImage && !x.IsSceneImageStale)
                .Select(x => new { x.Id, x.FriendlyName, x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY, x.SceneImageCropHash })
                .ToListAsync(cancellationToken));
            Directory.CreateDirectory(captures);
            foreach (var room in rooms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SceneBounds.TryGetSize(room.SceneMinX, room.SceneMinY, room.SceneMaxX, room.SceneMaxY, out _, out _)) continue;
                using var input = images.OpenRead(room.Id);
                if (input is null) continue;
                var path = Path.Combine(captures, $"{room.Id:D}.webp");
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await timing.MeasureAsync(PublishingMetric.CaptureCopy, () => input.CopyToAsync(output, cancellationToken));
                    timing.Bytes = (timing.Bytes ?? 0) + output.Position;
                    timing.Measure(PublishingMetric.CaptureFlush, () => output.Flush(true));
                }
                timing.Files = (timing.Files ?? 0) + 1;
                if (room.SceneImageCropHash is not null && !timing.Measure(PublishingMetric.CaptureValidation, () => SceneImageFileService.Matches(path, room.SceneImageCropHash)))
                    throw new PublishingBlock($"Frozen room {room.FriendlyName} ({room.Id:D}): copied capture Staging/Captures/{room.Id:D}.webp does not match the snapshot's crop identity.\nExpected crop hash: {room.SceneImageCropHash}\nReview/regenerate this room's capture in the editor, then discard the failed preparation and prepare again. The frozen database and copied image did not agree; the source of that mismatch is not established.");
            }
            return factory;
        }
    }
}
