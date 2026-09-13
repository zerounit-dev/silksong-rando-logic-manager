#if DEBUG
using Microsoft.EntityFrameworkCore;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class DebugDiagnosticSwitches
{
    private int circuitTimingEnabled = 1;
    private int efContextSummariesEnabled = 1;
    private int efCommandSqlEnabled;

    public bool CircuitTimingEnabled
    {
        get => Volatile.Read(ref circuitTimingEnabled) != 0;
        set => Interlocked.Exchange(ref circuitTimingEnabled, value ? 1 : 0);
    }

    public bool EfContextSummariesEnabled
    {
        get => Volatile.Read(ref efContextSummariesEnabled) != 0;
        set => Interlocked.Exchange(ref efContextSummariesEnabled, value ? 1 : 0);
    }

    public bool EfCommandSqlEnabled
    {
        get => Volatile.Read(ref efCommandSqlEnabled) != 0;
        set => Interlocked.Exchange(ref efCommandSqlEnabled, value ? 1 : 0);
    }
}

public static class DebugDiagnosticLogging
{
    public static ILoggingBuilder AddDebugDiagnosticFilters(
        this ILoggingBuilder logging,
        DebugDiagnosticSwitches switches)
    {
        logging.AddFilter(typeof(CircuitInboundTimingHandler).FullName!, level => level >= LogLevel.Debug);
        logging.AddFilter(typeof(DebugEfContextDiagnosticInterceptor).FullName!, level => level >= LogLevel.Debug);
        return logging.AddFilter(
            DbLoggerCategory.Database.Command.Name,
            level => level >= LogLevel.Warning ||
                (level == LogLevel.Information && switches.EfCommandSqlEnabled));
    }
}
#endif
