using System.Diagnostics;
using System.Globalization;

namespace SilksongRandoLogicManager.Contracts.Diagnostics;

// Browser invocation boundaries opt in. Shared algorithms and server hosts do not.
// Labels are code-owned constants; never supply application identifiers or text.
public sealed class OperationTiming : IDisposable
{
    private static long sequence;
    private static readonly AsyncLocal<OperationTiming?> ambient = new();
    private readonly OperationTiming? enclosing;
    private readonly string stage;
    private readonly long started;
    private readonly bool enabled;
    private bool ended;
    public long Id { get; }
    public long Parent { get; }
    public long ActiveId => ended ? 0 : Id;

    private OperationTiming(string stage, bool enabled, long parent, bool attach, long? started)
    {
        this.stage = stage; this.enabled = enabled;
        var current = ambient.Value;
        while (current?.ended == true) current = current.enclosing;
        enclosing = attach ? current : null;
        Parent = parent != 0 ? parent : enclosing?.Id ?? 0;
        Id = enabled ? Interlocked.Increment(ref sequence) : 0;
        this.started = started ?? Stopwatch.GetTimestamp();
        if (enabled && attach) ambient.Value = this;
        if (enabled) Write("start", 0);
    }

    public static OperationTiming Start(string stage, bool enabled = true, long parent = 0) => new(stage, enabled, parent, true, null);
    // A bounded owner may retain an observed wall interval until its existing render
    // callback. It is not an ambient CPU scope and creates no scheduling obligation.
    public static OperationTiming Observe(string stage, long parent = 0, long? started = null) => new(stage, true, parent, false, started);
    public void End(TimingOutcome outcome = TimingOutcome.Complete)
    {
        if (ReferenceEquals(ambient.Value, this)) ambient.Value = enclosing;
        if (ended) return;
        ended = true;
        if (enabled) Write(outcome.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    public void Dispose() => End(TimingOutcome.FailedOrRetired);
    private void Write(string outcome, double milliseconds)
    {
        try { Console.WriteLine($"[v3-operation] span={Id} parent={Parent} stage={stage} outcome={outcome} wall-ms={milliseconds.ToString("F1", CultureInfo.InvariantCulture)}"); }
        catch { } // Diagnostics must not replace an application outcome.
    }
}

public enum TimingOutcome
{
    Complete, Returned, FailedOrRetired, Retired, Rejected, ReviewRequired, Uncertain,
    KnownCommitFollowupFailed, CommittedFollowupReturned, Missing, Skipped, ResponseDecoded, Incorporated, Acknowledged
}

public enum ObservedWork { Tables, Header, RoomReferences, SubroomReferences, NativeSnapshot, NativeReconcile }

// Fixed-size numeric state only. No rows, native values, clean-state cache or history.
public sealed class OperationWork
{
    private readonly long[] counts = new long[6];
    private readonly double[] milliseconds = new double[6];
    public Measurement Measure(ObservedWork work) => new(this, work);
    public void Reset() { Array.Clear(counts); Array.Clear(milliseconds); }
    public void Report(long parent)
    {
        if (parent == 0) return;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            try { Console.WriteLine($"[v3-operation] parent={parent} observed-work={(ObservedWork)i} calls={counts[i]} aggregate-ms={milliseconds[i].ToString("F1", CultureInfo.InvariantCulture)}"); }
            catch { }
        }
        Reset();
    }
    public readonly struct Measurement : IDisposable
    {
        private readonly OperationWork owner;
        private readonly int work;
        private readonly long started;
        internal Measurement(OperationWork owner, ObservedWork work) { this.owner = owner; this.work = (int)work; started = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            owner.counts[work] = Math.Min(long.MaxValue - 1, owner.counts[work]) + 1;
            owner.milliseconds[work] = Math.Min(double.MaxValue, owner.milliseconds[work] + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
