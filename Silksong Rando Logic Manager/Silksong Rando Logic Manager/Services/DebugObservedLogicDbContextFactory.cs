#if DEBUG
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class DebugObservedLogicDbContextFactory(
    IDbContextFactory<LogicDbContext> inner,
    DebugEfContextDiagnosticInterceptor diagnostics) : IDbContextFactory<LogicDbContext>
{
    public LogicDbContext CreateDbContext()
    {
        var createdAt = Stopwatch.GetTimestamp();
        var context = inner.CreateDbContext();
        DebugLogicDbContextLifecycle.Register(context, diagnostics, createdAt);
        return context;
    }

    public async Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var createdAt = Stopwatch.GetTimestamp();
        var context = await inner.CreateDbContextAsync(cancellationToken);
        DebugLogicDbContextLifecycle.Register(context, diagnostics, createdAt);
        return context;
    }
}

internal static class DebugLogicDbContextLifecycle
{
    private static readonly ConditionalWeakTable<LogicDbContext, DebugEfContextDiagnosticInterceptor> Observers = new();

    public static void Register(LogicDbContext context, DebugEfContextDiagnosticInterceptor diagnostics, long createdAt)
    {
        diagnostics.Observe(context, createdAt);
        Observers.Add(context, diagnostics);
    }

    public static void Disposed(LogicDbContext context)
    {
        if (!Observers.TryGetValue(context, out var diagnostics)) return;
        Observers.Remove(context);
        diagnostics.ContextDisposed(context);
    }
}
#endif
