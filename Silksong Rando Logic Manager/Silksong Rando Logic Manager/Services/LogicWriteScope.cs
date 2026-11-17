using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>One original application transaction and its admitted capture/completion/
/// commit publication. The caller's existing image/publication admission encloses
/// this scope; it is not reacquired or released ahead of a reliable receipt.</summary>
internal sealed class LogicWriteScope : IAsyncDisposable
{
    private readonly LogicDbContext db;
    private readonly SharedPublicationCoordinator.Admission admission;
    private readonly LogicCacheHistory history;
    private readonly RecordHistoryStore? records;
    private readonly HistoryActor actor;
    private readonly IDbContextTransaction transaction;
    private readonly LogicChangeCapture capture;
    private readonly SqliteConnection connection;
    private IReadOnlyList<LogicChangeCapture.Change>? final;
    private LogicFinalEffects? effects;
    internal LogicFinalEffects FinalEffects => effects
        ?? throw new InvalidOperationException("Logic effects require successful completion/freeze finalization.");
    private AppliedCompletionIndex.Pending? completion;
    private bool commitAttempted;
    private bool disposed;

    private LogicWriteScope(LogicDbContext db, SharedPublicationCoordinator.Admission admission,
        LogicCacheHistory history, RecordHistoryStore? records, HistoryActor actor, IDbContextTransaction transaction, LogicChangeCapture capture)
    {
        this.db = db; this.admission = admission; this.history = history;
        this.transaction = transaction; this.capture = capture;
        this.records = records; this.actor = actor;
        connection = (SqliteConnection)db.Database.GetDbConnection();
    }

    internal static async Task<LogicWriteScope> BeginAsync(LogicDbContext db,
        SharedPublicationCoordinator.Admission admission, HistoryActor actor, CancellationToken token)
    {
        var records = admission.RequireRecordHistory(actor);
        var history = admission.Cache;
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Logic write scopes cannot nest or replace an existing transaction.");
        // EF's transaction adds/removes its own open count. Our additional lease
        // pins the SAME physical handle until rollback/disposal and TEMP cleanup.
        await db.Database.OpenConnectionAsync(token);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        IDbContextTransaction? transaction = null;
        LogicChangeCapture? capture = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(token);
            capture = LogicChangeCapture.Create(connection,
                (SqliteTransaction)transaction.GetDbTransaction(), history.Schema);
            await capture.InstallAsync(token);
            return new(db, admission, history, records, actor, transaction, capture);
        }
        catch
        {
            try
            {
                // Provider Begin/logging may have failed without returning its
                // transaction. Its physical state cannot be certified for reuse.
                if (transaction is null) SqliteConnection.ClearPool(connection);
            }
            finally { await ReleaseAsync(db, connection, transaction, capture); }
            throw;
        }
    }

    /// <summary>Call before ALL after-flags/affected IDs/projections/receipt planning.
    /// No source writes or SaveChanges may follow. Freeze guards raw SQL as well.</summary>
    internal async Task FinalizeAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        _ = admission.Cache; // Admission must remain held throughout the transaction.
        if (commitAttempted) throw new InvalidOperationException("Logic commit already attempted.");
        if (final is not null) return;
        var source = await capture.ReadAsync(token);
        completion = await LogicCompletionMaintenance.FinalizeAsync(db, history.Completion, source, token);
        await capture.FreezeAsync(token);
        var capturedFinal = await capture.ReadAsync(token);
        var finalizedEffects = new LogicFinalEffects(capturedFinal);
        final = capturedFinal;
        effects = finalizedEffects;
    }

    internal async Task<HistoryWriteOutcome> CommitAsync(Action? recordReliableReceipt = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        _ = admission.Cache;
        if (commitAttempted || final is null || effects is null || completion is null)
            throw new InvalidOperationException("Logic commit requires one successfully finalized transaction.");
        // A failed/uncertain commit is never automatically retried. Existing caller
        // recovery retains/invalidate its receipt/tracker evidence as appropriate.
        commitAttempted = true;
        await transaction.CommitAsync(token);
        var historyOutcome = HistoryWriteOutcome.Success;
        try { recordReliableReceipt?.Invoke(); }
        finally
        {
            try { history.PublishCommitted(final, completion); }
            finally
            {
                // Never use request cancellation here; callback/cache errors retain
                // their original propagation while independent history is attempted.
                if (records is not null) historyOutcome = await records.AppendCommittedAsync(actor, final);
            }
        }
        return historyOutcome;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await ReleaseAsync(db, connection, transaction, capture); }
        finally { final = null; effects = null; completion = null; }
    }

    private static async Task ReleaseAsync(LogicDbContext db, SqliteConnection connection,
        IDbContextTransaction? transaction, LogicChangeCapture? capture)
    {
        // EF10 BeginTransaction opens before attempting the provider Begin. If
        // Begin throws, no transaction exists to balance that implicit open count.
        var implicitLease = transaction is null;
        try
        {
            try
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }
            catch
            {
                // A disposal failure before ClearTransaction leaves EF's implicit
                // count outstanding. Detach only our transaction; never the caller's.
                implicitLease = ReferenceEquals(db.Database.CurrentTransaction, transaction);
                SqliteConnection.ClearPool(connection);
                throw;
            }
            finally
            {
                // Always after the rollback/disposal attempt, including partial
                // installation. Never let cleanup bypass lease release below.
                try { if (capture is not null) await capture.DisposeAsync(); }
                catch
                {
                    SqliteConnection.ClearPool(connection);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                if (implicitLease)
                {
                    try
                    {
                        if (transaction is not null) await db.Database.UseTransactionAsync(null);
                    }
                    finally { await db.Database.CloseConnectionAsync(); }
                }
            }
            catch
            {
                SqliteConnection.ClearPool(connection);
                throw;
            }
            finally
            {
                try { await db.Database.CloseConnectionAsync(); }
                catch
                {
                    // Release failure is also uncertain; do not pool this handle.
                    SqliteConnection.ClearPool(connection);
                    throw;
                }
            }
        }
    }
}
