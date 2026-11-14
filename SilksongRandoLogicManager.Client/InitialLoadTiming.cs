using System.Diagnostics;
using System.Globalization;

namespace SilksongRandoLogicManager.Client;

// Only initial managed startup calls this helper. No scheduling or application error policy.
internal sealed class InitialLoadTiming : IDisposable
{
    private readonly Func<bool>? owns;
    private string? stage;
    private long started;

    internal InitialLoadTiming(string stage, bool enabled = true, Func<bool>? owns = null)
    {
        this.owns = owns;
        if (enabled) Start(stage);
    }

    internal void Next(string next, bool current = true)
    {
        if (stage is null) return;
        if (!current || !(owns?.Invoke() ?? true)) { Dispose(); return; }
        Complete();
        Start(next);
    }

    private void Start(string name)
    {
        stage = name;
        started = Stopwatch.GetTimestamp();
        Write("start", 0);
    }

    internal void Complete(bool current = true)
    {
        if (stage is null) return;
        Write(current && (owns?.Invoke() ?? true) ? "complete" : "failed-or-retired",
            Math.Max(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        stage = null;
    }

    public void Dispose()
    {
        if (stage is null) return;
        Write("failed-or-retired", Math.Max(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        stage = null;
    }

    private void Write(string outcome, double milliseconds)
    {
        // A console sink failure must neither replace nor create an application failure.
        try { Console.WriteLine($"[initial-load] {stage}: {outcome} {milliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms"); }
        catch { }
    }
}
