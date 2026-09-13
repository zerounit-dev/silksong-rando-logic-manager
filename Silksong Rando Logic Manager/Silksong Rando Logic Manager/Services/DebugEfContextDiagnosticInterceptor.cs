#if DEBUG
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class DebugEfContextDiagnosticInterceptor : IDbCommandInterceptor
{
    private readonly ConcurrentDictionary<Guid, ContextState> states = new();
    private readonly ILogger<DebugEfContextDiagnosticInterceptor> logger;
    private readonly DebugDiagnosticSwitches switches;

    public DebugEfContextDiagnosticInterceptor(
        ILogger<DebugEfContextDiagnosticInterceptor> logger,
        DebugDiagnosticSwitches switches)
    {
        this.logger = logger;
        this.switches = switches;
    }

    public int TrackedContextCount => states.Count;

    public void Observe(LogicDbContext context, long createdAt)
    {
        states.TryAdd(context.ContextId.InstanceId, new(context.ContextId.ToString(), createdAt));
    }

    public void ContextDisposed(LogicDbContext context) => Complete(context);

    public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Successful(eventData, CommandKind.Reader);
        return result;
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Successful(eventData, CommandKind.Reader);
        return ValueTask.FromResult(result);
    }

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Successful(eventData, CommandKind.Scalar);
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        Successful(eventData, CommandKind.Scalar);
        return ValueTask.FromResult(result);
    }

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Successful(eventData, CommandKind.NonQuery);
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Successful(eventData, CommandKind.NonQuery);
        return ValueTask.FromResult(result);
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Failed(eventData);

    public Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Failed(eventData);
        return Task.CompletedTask;
    }

    public void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Cancelled(eventData);

    public Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Cancelled(eventData);
        return Task.CompletedTask;
    }

    private void Successful(CommandExecutedEventData eventData, CommandKind kind)
    {
        if (Find(eventData.Context) is not { } state) return;
        state.AddDuration(eventData.Duration);
        switch (kind)
        {
            case CommandKind.Reader: Interlocked.Increment(ref state.ReaderCommands); break;
            case CommandKind.Scalar: Interlocked.Increment(ref state.ScalarCommands); break;
            case CommandKind.NonQuery: Interlocked.Increment(ref state.NonQueryCommands); break;
        }
    }

    private void Failed(CommandErrorEventData eventData)
    {
        if (Find(eventData.Context) is not { } state) return;
        state.AddDuration(eventData.Duration);
        Interlocked.Increment(ref state.FailedCommands);
    }

    private void Cancelled(CommandEndEventData eventData)
    {
        if (Find(eventData.Context) is not { } state) return;
        state.AddDuration(eventData.Duration);
        Interlocked.Increment(ref state.CancelledCommands);
    }

    private ContextState? Find(DbContext? context) =>
        context is not null && states.TryGetValue(context.ContextId.InstanceId, out var state) ? state : null;

    private void Complete(DbContext? context)
    {
        if (context is null || !states.TryRemove(context.ContextId.InstanceId, out var state)) return;
        if (!switches.EfContextSummariesEnabled) return;

        var readerCommands = Volatile.Read(ref state.ReaderCommands);
        var scalarCommands = Volatile.Read(ref state.ScalarCommands);
        var nonQueryCommands = Volatile.Read(ref state.NonQueryCommands);
        var failedCommands = Volatile.Read(ref state.FailedCommands);
        var cancelledCommands = Volatile.Read(ref state.CancelledCommands);

        LogContextSummary(
            state.ContextId,
            Math.Max(0, Stopwatch.GetElapsedTime(state.CreatedAt).TotalMilliseconds),
            readerCommands + scalarCommands + nonQueryCommands + failedCommands + cancelledCommands,
            readerCommands,
            scalarCommands,
            nonQueryCommands,
            Math.Max(0, TimeSpan.FromTicks(Volatile.Read(ref state.ProviderDurationTicks)).TotalMilliseconds),
            failedCommands,
            cancelledCommands);
    }

    private enum CommandKind { Reader, Scalar, NonQuery }

    [LoggerMessage(
        EventId = 1,
        EventName = "EfContextDisposedSummary",
        Level = LogLevel.Debug,
        Message = "{LifetimeMilliseconds:F3} ms context | {CommandAttempts} commands | {ProviderCommandMilliseconds:F3} ms provider SQL | readers {ReaderCommands}, scalars {ScalarCommands}, nonqueries {NonQueryCommands}, failed {FailedCommands}, cancelled {CancelledCommands} | context {ContextId}")]
    private partial void LogContextSummary(
        string contextId,
        double lifetimeMilliseconds,
        long commandAttempts,
        long readerCommands,
        long scalarCommands,
        long nonQueryCommands,
        double providerCommandMilliseconds,
        long failedCommands,
        long cancelledCommands);

    private sealed class ContextState(string contextId, long createdAt)
    {
        public string ContextId { get; } = contextId;
        public long CreatedAt { get; } = createdAt;
        public long ReaderCommands;
        public long ScalarCommands;
        public long NonQueryCommands;
        public long ProviderDurationTicks;
        public long FailedCommands;
        public long CancelledCommands;

        public void AddDuration(TimeSpan duration) =>
            Interlocked.Add(ref ProviderDurationTicks, Math.Max(0, duration.Ticks));
    }
}
#endif
