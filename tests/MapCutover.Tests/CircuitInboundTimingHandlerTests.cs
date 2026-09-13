using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace MapCutover.Tests;

public sealed class CircuitInboundTimingHandlerTests
{
    private const string CircuitId = "test-circuit";
    private static readonly HashSet<string> StartKeys =
        ["OperationId", "CircuitId", "ActivitySequence", "{OriginalFormat}"];
    private static readonly HashSet<string> TerminalKeys =
        ["TerminalState", "OperationId", "CircuitId", "ActivitySequence", "ElapsedMilliseconds", "{OriginalFormat}"];
    private static readonly HashSet<string> FailureKeys =
        ["TerminalState", "OperationId", "CircuitId", "ActivitySequence", "ElapsedMilliseconds", "ExceptionType", "{OriginalFormat}"];

    [Fact]
    public async Task SuccessfulActivity_LogsOneCorrelatedPairAndInvokesContinuationOnce()
    {
        var logger = new CapturingLogger<CircuitInboundTimingHandler>();
        var handler = new CircuitInboundTimingHandler(logger, new());
        var context = CreateContext(CircuitId);
        var invocations = 0;
        var wrapped = handler.CreateInboundActivityHandler(activity =>
        {
            Assert.Same(context, activity);
            invocations++;
            return Task.CompletedTask;
        });

        await wrapped(context);

        Assert.Equal(1, invocations);
        AssertPair(logger.Entries, "completed", TerminalKeys);
    }

    [Fact]
    public async Task OverlappingActivities_HaveMonotonicSequencesAndPairBySequenceAndOperation()
    {
        var logger = new CapturingLogger<CircuitInboundTimingHandler>();
        var handler = new CircuitInboundTimingHandler(logger, new());
        var context = CreateContext(CircuitId);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissionCount = 0;
        var bothAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapped = handler.CreateInboundActivityHandler(async _ =>
        {
            var admission = Interlocked.Increment(ref admissionCount);
            if (admission == 2) bothAdmitted.SetResult();
            await (admission == 1 ? firstRelease.Task : secondRelease.Task);
        });

        var first = wrapped(context);
        var second = wrapped(context);
        await bothAdmitted.Task;
        secondRelease.SetResult();
        await second;
        firstRelease.SetResult();
        await first;

        Assert.Equal([1L, 2L], logger.Entries.Where(entry => entry.EventId.Name == "CircuitInboundActivityStarted").Select(entry => Assert.IsType<long>(entry.Properties["ActivitySequence"])));
        foreach (var sequence in new[] { 1L, 2L })
        {
            var pair = logger.Entries.Where(entry => Equals(entry.Properties["ActivitySequence"], sequence)).ToArray();
            Assert.Equal(2, pair.Length);
            Assert.Equal(pair[0].Properties["OperationId"], pair[1].Properties["OperationId"]);
            Assert.Equal($"START | circuit activity #{sequence}", pair[0].Message);
            var expectedTerminal = FormattableString.Invariant($"{Assert.IsType<double>(pair[1].Properties["ElapsedMilliseconds"]):F3} ms | circuit completed | activity #{sequence}");
            Assert.Equal(expectedTerminal, pair[1].Message);
        }
    }

    [Fact]
    public async Task FailedActivity_LogsFailedTerminalAndRethrowsOriginalException()
    {
        var logger = new CapturingLogger<CircuitInboundTimingHandler>();
        var handler = new CircuitInboundTimingHandler(logger, new());
        var context = CreateContext(CircuitId);
        var expected = new PayloadBearingException("private authored value");
        var invocations = 0;
        var wrapped = handler.CreateInboundActivityHandler(_ =>
        {
            invocations++;
            return Task.FromException(expected);
        });

        var actual = await Record.ExceptionAsync(() => wrapped(context));

        Assert.Same(expected, actual);
        Assert.Equal(1, invocations);
        AssertPair(logger.Entries, "failed", FailureKeys);
        Assert.Equal(typeof(PayloadBearingException).FullName, logger.Entries[1].Properties["ExceptionType"]);
        Assert.EndsWith($" | exception {typeof(PayloadBearingException).FullName}", logger.Entries[1].Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(expected.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledActivity_LogsCancelledTerminalAndRethrowsOriginalCancellation()
    {
        var logger = new CapturingLogger<CircuitInboundTimingHandler>();
        var handler = new CircuitInboundTimingHandler(logger, new());
        var context = CreateContext(CircuitId);
        var expected = new OperationCanceledException("private cancellation value");
        var invocations = 0;
        var wrapped = handler.CreateInboundActivityHandler(_ =>
        {
            invocations++;
            return Task.FromException(expected);
        });

        var actual = await Record.ExceptionAsync(() => wrapped(context));

        Assert.Same(expected, actual);
        Assert.Equal(1, invocations);
        AssertPair(logger.Entries, "cancelled", TerminalKeys);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(expected.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnablementIsSnapshottedAtAdmissionAndSwitchesIndependently()
    {
        var logger = new CapturingLogger<CircuitInboundTimingHandler>();
        var switches = new DebugDiagnosticSwitches { CircuitTimingEnabled = false };
        var handler = new CircuitInboundTimingHandler(logger, switches);
        var context = CreateContext(CircuitId);
        var disabledInvocations = 0;
        var disabled = handler.CreateInboundActivityHandler(_ =>
        {
            disabledInvocations++;
            switches.CircuitTimingEnabled = true;
            return Task.CompletedTask;
        });

        await disabled(context);
        Assert.Equal(1, disabledInvocations);
        Assert.Empty(logger.Entries);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enabled = handler.CreateInboundActivityHandler(async _ =>
        {
            admitted.SetResult();
            await release.Task;
        });
        var operation = enabled(context);
        await admitted.Task;
        switches.CircuitTimingEnabled = false;
        release.SetResult();
        await operation;

        AssertPair(logger.Entries, "completed", TerminalKeys);
        Assert.True(switches.EfContextSummariesEnabled);
        Assert.False(switches.EfCommandSqlEnabled);
    }

    [Fact]
    public void ProductionRegistration_ContainsExactlyOneCircuitHandlerAndNoBroaderTracingMechanism()
    {
        var projectRoot = FindProjectRoot();
        var program = File.ReadAllText(Path.Combine(projectRoot, "Program.cs"));
        var applicationRoot = Directory.GetParent(projectRoot)!.FullName;
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        Assert.Equal(1, Count(program, "AddScoped<CircuitHandler, CircuitInboundTimingHandler>()"));
        Assert.Equal(1, Count(source, "AddScoped<CircuitHandler"));
        Assert.Equal(1, Count(program, "builder.Services.AddLogicDbContextFactory(databasePath);"));
        Assert.Equal(1, Count(program, "builder.Services.AddDbContextFactory<LogicDbContext>(options => options.UseSqlite($\"Data Source={databasePath}\"));"));
        var project = File.ReadAllText(Path.Combine(projectRoot, "Silksong Rando Logic Manager.csproj"));
        Assert.Contains("<Compile Remove=\"Data\\LogicDbContextFactoryRegistration.cs\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivitySource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddOpenTelemetry", source, StringComparison.Ordinal);
        Assert.Contains("#if DEBUG", program, StringComparison.Ordinal);
    }

    private static void AssertPair(
        IReadOnlyList<LogEntry> entries,
        string terminalState,
        HashSet<string> terminalKeys)
    {
        Assert.Equal(2, entries.Count);
        var start = entries[0];
        var terminal = entries[1];
        Assert.Equal(LogLevel.Debug, start.Level);
        Assert.Equal(LogLevel.Debug, terminal.Level);
        Assert.Equal("CircuitInboundActivityStarted", start.EventId.Name);
        Assert.Equal("CircuitInboundActivityTerminated", terminal.EventId.Name);
        Assert.Equal(terminalState, terminal.Properties["TerminalState"]);
        Assert.Equal(start.Properties["OperationId"], terminal.Properties["OperationId"]);
        Assert.NotEqual(Guid.Empty, Assert.IsType<Guid>(start.Properties["OperationId"]));
        Assert.Equal(CircuitId, start.Properties["CircuitId"]);
        Assert.Equal(CircuitId, terminal.Properties["CircuitId"]);
        Assert.True(Assert.IsType<double>(terminal.Properties["ElapsedMilliseconds"]) >= 0);
        Assert.Equal(start.Properties["ActivitySequence"], terminal.Properties["ActivitySequence"]);
        var sequence = Assert.IsType<long>(start.Properties["ActivitySequence"]);
        Assert.Equal($"START | circuit activity #{sequence}", start.Message);
        var elapsedPrefix = FormattableString.Invariant($"{Assert.IsType<double>(terminal.Properties["ElapsedMilliseconds"]):F3} ms | circuit {terminalState} | activity #{sequence}");
        Assert.StartsWith(elapsedPrefix, terminal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.IsType<Guid>(start.Properties["OperationId"]).ToString(), start.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CircuitId, start.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.IsType<Guid>(terminal.Properties["OperationId"]).ToString(), terminal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CircuitId, terminal.Message, StringComparison.Ordinal);
        Assert.Equal(StartKeys, start.Properties.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(terminalKeys, terminal.Properties.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private static CircuitInboundActivityContext CreateContext(string circuitId)
    {
        var assembly = typeof(Circuit).Assembly;
        var circuitIdType = assembly.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitId", true)!;
        var circuitHostType = assembly.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost", true)!;
        var id = Activator.CreateInstance(
            circuitIdType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["test-secret", circuitId],
            culture: null)!;
        var host = RuntimeHelpers.GetUninitializedObject(circuitHostType);
        circuitHostType.GetField("<CircuitId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, id);
        var circuit = (Circuit)Activator.CreateInstance(
            typeof(Circuit),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [host],
            culture: null)!;
        return (CircuitInboundActivityContext)Activator.CreateInstance(
            typeof(CircuitInboundActivityContext),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [new Func<Task>(() => Task.CompletedTask), circuit],
            culture: null)!;
    }

    private static string FindProjectRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager");
            if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate the application project root.");
    }

    private static int Count(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private sealed class PayloadBearingException(string message) : Exception(message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(state)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            Entries.Add(new(logLevel, eventId, properties, exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception,
        string Message);
}
