using System.Diagnostics;

namespace Silksong_Rando_Logic_Manager.Services;

// Optional test instrumentation for scoped resolver stages.
public sealed class ScopedResolverTrace
{
    private readonly Dictionary<string, TimeSpan> stages = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TimeSpan> Stages => stages;

    public void Reset() => stages.Clear();

    public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation();
        Add(stage, stopwatch.Elapsed);
        return result;
    }

    public async Task MeasureAsync(string stage, Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        await operation();
        Add(stage, stopwatch.Elapsed);
    }

    public void Measure(string stage, Action operation)
    {
        var stopwatch = Stopwatch.StartNew();
        operation();
        Add(stage, stopwatch.Elapsed);
    }

    private void Add(string stage, TimeSpan elapsed) => stages[stage] = stages.GetValueOrDefault(stage) + elapsed;
}
