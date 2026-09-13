#if DEBUG
using System.Diagnostics;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class CircuitInboundTimingHandler(
    ILogger<CircuitInboundTimingHandler> logger,
    DebugDiagnosticSwitches switches) : CircuitHandler
{
    private static readonly EventId ActivityStartedEvent = new(1, "CircuitInboundActivityStarted");
    private static readonly EventId ActivityTerminatedEvent = new(2, "CircuitInboundActivityTerminated");
    private long activitySequence;

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return context => InvokeAsync(next, context);
    }

    private async Task InvokeAsync(
        Func<CircuitInboundActivityContext, Task> next,
        CircuitInboundActivityContext context)
    {
        var timingEnabledAtAdmission = switches.CircuitTimingEnabled;
        if (!timingEnabledAtAdmission)
        {
            await next(context);
            return;
        }

        var operationId = Guid.NewGuid();
        var circuitId = context.Circuit.Id;
        var sequence = Interlocked.Increment(ref activitySequence);
        var startedAt = Stopwatch.GetTimestamp();

        LogActivityStarted(operationId, circuitId, sequence);

        try
        {
            await next(context);
            LogActivityTerminated(
                "completed",
                operationId,
                circuitId,
                sequence,
                ElapsedMilliseconds(startedAt),
                null);
        }
        catch (OperationCanceledException)
        {
            LogActivityTerminated(
                "cancelled",
                operationId,
                circuitId,
                sequence,
                ElapsedMilliseconds(startedAt),
                null);
            throw;
        }
        catch (Exception exception)
        {
            LogActivityTerminated(
                "failed",
                operationId,
                circuitId,
                sequence,
                ElapsedMilliseconds(startedAt),
                exception.GetType().FullName ?? exception.GetType().Name);
            throw;
        }
    }

    private static double ElapsedMilliseconds(long startedAt) =>
        Math.Max(0, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    private void LogActivityStarted(Guid operationId, string circuitId, long sequence)
    {
        var state = new ActivityLogState(
            $"START | circuit activity #{sequence}",
            [
                new("OperationId", operationId),
                new("CircuitId", circuitId),
                new("ActivitySequence", sequence),
                new("{OriginalFormat}", "START | circuit activity #{ActivitySequence}")
            ]);
        logger.Log(LogLevel.Debug, ActivityStartedEvent, state, null, static (value, _) => value.Message);
    }

    private void LogActivityTerminated(
        string terminalState,
        Guid operationId,
        string circuitId,
        long sequence,
        double elapsedMilliseconds,
        string? exceptionType)
    {
        var message = FormattableString.Invariant($"{elapsedMilliseconds:F3} ms | circuit {terminalState} | activity #{sequence}");
        if (exceptionType is not null) message += $" | exception {exceptionType}";
        var properties = new List<KeyValuePair<string, object?>>
        {
            new("TerminalState", terminalState),
            new("OperationId", operationId),
            new("CircuitId", circuitId),
            new("ActivitySequence", sequence),
            new("ElapsedMilliseconds", elapsedMilliseconds)
        };
        if (exceptionType is not null) properties.Add(new("ExceptionType", exceptionType));
        properties.Add(new("{OriginalFormat}", exceptionType is null
            ? "{ElapsedMilliseconds:F3} ms | circuit {TerminalState} | activity #{ActivitySequence}"
            : "{ElapsedMilliseconds:F3} ms | circuit {TerminalState} | activity #{ActivitySequence} | exception {ExceptionType}"));
        var state = new ActivityLogState(message, properties);
        logger.Log(LogLevel.Debug, ActivityTerminatedEvent, state, null, static (value, _) => value.Message);
    }

    private sealed class ActivityLogState(
        string message,
        IReadOnlyList<KeyValuePair<string, object?>> properties) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;
        public int Count => properties.Count;
        public KeyValuePair<string, object?> this[int index] => properties[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => properties.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => Message;
    }
}
#endif
