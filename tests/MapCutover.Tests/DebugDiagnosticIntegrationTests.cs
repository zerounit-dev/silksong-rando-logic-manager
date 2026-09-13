using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace MapCutover.Tests;

[Collection("Debug diagnostic isolation")]
public sealed class DebugDiagnosticIntegrationTests
{
    [Fact]
    public async Task ProductionFactory_LogsOneAccurateSummaryPerDisposedRealSqliteContextAndRemovesState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silksong-debug-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "logic.db");
        await using (var setup = new SqliteConnection($"Data Source={databasePath}"))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE Rooms (FriendlyName TEXT NOT NULL); CREATE TABLE RoomGroups (Id TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var logs = new CaptureLoggerProvider();
        var switches = new DebugDiagnosticSwitches();
        using var providerCommands = new ProviderCommandObserver();
        var services = BuildServices(databasePath, switches, logs);
        try
        {
            var factory = services.GetRequiredService<IDbContextFactory<LogicDbContext>>();
            var interceptor = services.GetRequiredService<DebugEfContextDiagnosticInterceptor>();

            string firstId;
            var firstContext = await factory.CreateDbContextAsync();
            firstId = firstContext.ContextId.ToString();
            await firstContext.Rooms.AsNoTracking().CountAsync();
            await firstContext.Database.ExecuteSqlRawAsync("CREATE TABLE DiagnosticProbe (Id INTEGER NOT NULL)");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                firstContext.Database.ExecuteSqlRawAsync("INSERT INTO MissingDiagnosticTable VALUES (1)"));
            await firstContext.DisposeAsync();
            await firstContext.DisposeAsync();

            string secondId;
            await using (var context = await factory.CreateDbContextAsync())
            {
                secondId = context.ContextId.ToString();
                await context.RoomGroups.AsNoTracking().CountAsync();
            }

            var contextIds = new HashSet<string>([firstId, secondId], StringComparer.Ordinal);
            var summaries = logs.Entries
                .Where(entry => entry.EventId.Name == "EfContextDisposedSummary" && contextIds.Contains(SummaryString("ContextId")(entry)))
                .ToArray();
            Assert.Equal(2, summaries.Length);
            Assert.Equal([firstId, secondId], summaries.Select(SummaryString("ContextId")));
            Assert.All(summaries, summary =>
            {
                Assert.True(SummaryDouble(summary, "LifetimeMilliseconds") >= 0);
                Assert.True(SummaryDouble(summary, "ProviderCommandMilliseconds") >= 0);
                Assert.Equal(0L, SummaryLong(summary, "ScalarCommands"));
                Assert.Equal(0L, SummaryLong(summary, "CancelledCommands"));
                Assert.DoesNotContain("CommandText", summary.Properties.Keys);
                Assert.DoesNotContain("Exception", summary.Properties.Keys);
                Assert.Equal(
                    SummaryLong(summary, "ReaderCommands") +
                    SummaryLong(summary, "ScalarCommands") +
                    SummaryLong(summary, "NonQueryCommands") +
                    SummaryLong(summary, "FailedCommands") +
                    SummaryLong(summary, "CancelledCommands"),
                    SummaryLong(summary, "CommandAttempts"));
                var expectedLead = FormattableString.Invariant(
                    $"{SummaryDouble(summary, "LifetimeMilliseconds"):F3} ms context | {SummaryLong(summary, "CommandAttempts")} commands | {SummaryDouble(summary, "ProviderCommandMilliseconds"):F3} ms provider SQL | readers {SummaryLong(summary, "ReaderCommands")}, scalars {SummaryLong(summary, "ScalarCommands")}, nonqueries {SummaryLong(summary, "NonQueryCommands")}, failed {SummaryLong(summary, "FailedCommands")}, cancelled {SummaryLong(summary, "CancelledCommands")} | context ");
                Assert.StartsWith(expectedLead, summary.Message, StringComparison.Ordinal);
                Assert.EndsWith(SummaryString("ContextId")(summary), summary.Message, StringComparison.Ordinal);
            });
            Assert.Equal(1L, SummaryLong(summaries[0], "ReaderCommands"));
            Assert.Equal(1L, SummaryLong(summaries[0], "NonQueryCommands"));
            Assert.Equal(1L, SummaryLong(summaries[0], "FailedCommands"));
            Assert.Equal(1L, SummaryLong(summaries[1], "ReaderCommands"));
            Assert.Equal(0L, SummaryLong(summaries[1], "FailedCommands"));
            AssertSummaryMatchesProviderEvents(summaries[0], providerCommands.Get(firstId));
            AssertSummaryMatchesProviderEvents(summaries[1], providerCommands.Get(secondId));
            Assert.Equal(0, interceptor.TrackedContextCount);
            Assert.DoesNotContain(logs.Entries, entry =>
                entry.Category == DbLoggerCategory.Database.Command.Name && entry.Level == LogLevel.Information);
            Assert.Contains(logs.Entries, entry =>
                entry.Category == DbLoggerCategory.Database.Command.Name && entry.Level >= LogLevel.Warning);

            await using (var context = await factory.CreateDbContextAsync())
            {
                switches.EfContextSummariesEnabled = false;
                await context.Rooms.AsNoTracking().CountAsync();
            }
            Assert.Equal(2, logs.Entries.Count(entry => entry.EventId.Name == "EfContextDisposedSummary"));
            Assert.Equal(0, interceptor.TrackedContextCount);

            const string privateValue = "private-authored-value";
            switches.EfCommandSqlEnabled = true;
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Rooms.AsNoTracking().CountAsync(room => room.FriendlyName == EF.Parameter(privateValue));
            }
            Assert.Contains(logs.Entries, entry => entry.Category == DbLoggerCategory.Database.Command.Name && entry.Level == LogLevel.Information);
            Assert.DoesNotContain(logs.Entries, entry =>
                entry.Category == DbLoggerCategory.Database.Command.Name &&
                entry.Message.Contains(privateValue, StringComparison.Ordinal));

            await using var verification = new SqliteConnection($"Data Source={databasePath}");
            await verification.OpenAsync();
            await using var verifyCommand = verification.CreateCommand();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DiagnosticProbe'";
            Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
        }
        finally
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectInterceptorEventHarness_ProvesScalarAndCancellationTerminalAccounting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silksong-debug-event-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var logs = new CaptureLoggerProvider();
        var switches = new DebugDiagnosticSwitches();
        var services = BuildServices(Path.Combine(root, "logic.db"), switches, logs);
        try
        {
            var factory = services.GetRequiredService<IDbContextFactory<LogicDbContext>>();
            var interceptor = services.GetRequiredService<DebugEfContextDiagnosticInterceptor>();
            var context = await factory.CreateDbContextAsync();
            var contextId = context.ContextId.ToString();
            var scalarDuration = TimeSpan.FromMilliseconds(1.25);
            var cancellationDuration = TimeSpan.FromMilliseconds(2.75);
            using var command = new SqliteCommand();

            var scalarEvent = CreateEventData<CommandExecutedEventData>(context, scalarDuration);
            Assert.Equal(7, interceptor.ScalarExecuted(command, scalarEvent, 7));
            var cancellationEvent = CreateEventData<CommandEndEventData>(context, cancellationDuration);
            await interceptor.CommandCanceledAsync(command, cancellationEvent);
            await context.DisposeAsync();
            await context.DisposeAsync();

            var summary = Assert.Single(logs.Entries, entry =>
                entry.EventId.Name == "EfContextDisposedSummary" &&
                Equals(entry.Properties["ContextId"], contextId));
            var expected = new ProviderCounts(
                Scalars: 1,
                Cancelled: 1,
                DurationTicks: scalarDuration.Ticks + cancellationDuration.Ticks);
            AssertSummaryMatchesProviderEvents(summary, expected);
            Assert.Equal(2L, SummaryLong(summary, "CommandAttempts"));
            Assert.Equal(0, interceptor.TrackedContextCount);
        }
        finally
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CommandSqlFilter_IsImmediateIndependentAndNeverRevealsSensitiveParameterValues()
    {
        var logs = new CaptureLoggerProvider();
        var switches = new DebugDiagnosticSwitches();
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(logs);
            logging.AddDebugDiagnosticFilters(switches);
        });
        var logger = factory.CreateLogger(DbLoggerCategory.Database.Command.Name);
        var circuitLogger = factory.CreateLogger<CircuitInboundTimingHandler>();
        var contextLogger = factory.CreateLogger<DebugEfContextDiagnosticInterceptor>();

        circuitLogger.LogDebug("circuit diagnostic category enabled");
        contextLogger.LogDebug("context diagnostic category enabled");
        Assert.Contains(logs.Entries, entry => entry.Message == "circuit diagnostic category enabled");
        Assert.Contains(logs.Entries, entry => entry.Message == "context diagnostic category enabled");

        logger.LogInformation("ordinary sql {Parameter}", "private-authored-value");
        logger.LogWarning("warning remains");
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning);

        switches.EfCommandSqlEnabled = true;
        logger.LogInformation("ordinary sql with redacted parameters");
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("private-authored-value", StringComparison.Ordinal));

        switches.CircuitTimingEnabled = false;
        switches.EfContextSummariesEnabled = false;
        logger.LogInformation("still enabled independently");
        Assert.Contains(logs.Entries, entry => entry.Message == "still enabled independently");
    }

    [Fact]
    public void DebugUi_RendersDefaultsChangesSwitchesImmediatelyClosesAndIsLastFooterControl()
    {
        using var context = new TestContext();
        var switches = new DebugDiagnosticSwitches();
        context.Services.AddSingleton(switches);
        var component = context.RenderComponent<DebugDiagnosticControls>();

        Assert.Equal("debug", component.Find("button").TextContent.Trim());
        component.Find("button").Click();
        var dialog = component.Find("[role=dialog]");
        Assert.Equal("debug diagnostics", dialog.QuerySelector("h2")!.TextContent.Trim());
        Assert.Contains("all current circuits", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("reset when the process restarts", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("circuit timing on, EF context summaries on, and ordinary EF command SQL off", dialog.TextContent, StringComparison.Ordinal);
        var toggles = component.FindAll("input[type=checkbox]").ToArray();
        Assert.Equal([true, true, false], toggles.Select(toggle => toggle.HasAttribute("checked")));
        toggles[0].Change(false);
        toggles[1].Change(false);
        toggles[2].Change(true);
        Assert.False(switches.CircuitTimingEnabled);
        Assert.False(switches.EfContextSummariesEnabled);
        Assert.True(switches.EfCommandSqlEnabled);
        Assert.Equal([false, false, true], component.FindAll("input[type=checkbox]").Select(toggle => toggle.HasAttribute("checked")));
        component.FindAll("button").Single(button => button.TextContent.Trim() == "close").Click();
        Assert.Empty(component.FindAll("[role=dialog]"));

        var secondCircuit = context.RenderComponent<DebugDiagnosticControls>();
        secondCircuit.Find("button").Click();
        Assert.Equal([false, false, true], secondCircuit.FindAll("input[type=checkbox]").Select(toggle => toggle.HasAttribute("checked")));

        var nav = File.ReadAllText(Path.Combine(FindProjectRoot(), "Components", "Layout", "NavMenu.razor"));
        Assert.True(nav.IndexOf("<DebugDiagnosticControls />", StringComparison.Ordinal) > nav.IndexOf("export rooms", StringComparison.Ordinal));
        Assert.True(nav.IndexOf("<DebugDiagnosticControls />", StringComparison.Ordinal) < nav.IndexOf("</footer>", StringComparison.Ordinal));
    }

    private static ServiceProvider BuildServices(string databasePath, DebugDiagnosticSwitches switches, CaptureLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton(switches);
        services.AddSingleton<DebugEfContextDiagnosticInterceptor>();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(logs);
            logging.AddDebugDiagnosticFilters(switches);
        });
        services.AddLogicDbContextFactory(databasePath);
        return services.BuildServiceProvider();
    }

    private static Func<CaptureLogEntry, string> SummaryString(string key) => entry => Assert.IsType<string>(entry.Properties[key]);
    private static long SummaryLong(CaptureLogEntry entry, string key) => Assert.IsType<long>(entry.Properties[key]);
    private static double SummaryDouble(CaptureLogEntry entry, string key) => Assert.IsType<double>(entry.Properties[key]);
    private static void AssertSummaryMatchesProviderEvents(CaptureLogEntry summary, ProviderCounts expected)
    {
        Assert.Equal(expected.Readers, SummaryLong(summary, "ReaderCommands"));
        Assert.Equal(expected.Scalars, SummaryLong(summary, "ScalarCommands"));
        Assert.Equal(expected.NonQueries, SummaryLong(summary, "NonQueryCommands"));
        Assert.Equal(expected.Failed, SummaryLong(summary, "FailedCommands"));
        Assert.Equal(expected.Cancelled, SummaryLong(summary, "CancelledCommands"));
        Assert.Equal(
            expected.Readers + expected.Scalars + expected.NonQueries + expected.Failed + expected.Cancelled,
            SummaryLong(summary, "CommandAttempts"));
        Assert.Equal(TimeSpan.FromTicks(expected.DurationTicks).TotalMilliseconds, SummaryDouble(summary, "ProviderCommandMilliseconds"));
    }

    private static T CreateEventData<T>(LogicDbContext context, TimeSpan duration) where T : CommandEndEventData
    {
        var eventData = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        SetSingleField(eventData, field => typeof(DbContext).IsAssignableFrom(field.FieldType), context, "context");
        SetSingleField(eventData, field => field.FieldType == typeof(TimeSpan), duration, "duration");
        Assert.Same(context, eventData.Context);
        Assert.Equal(duration, eventData.Duration);
        return eventData;
    }

    private static void SetSingleField(object target, Func<FieldInfo, bool> predicate, object value, string description)
    {
        var fields = new List<FieldInfo>();
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            fields.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(predicate));
        }
        var field = Assert.Single(fields);
        field.SetValue(target, value);
        Assert.NotNull(field.GetValue(target));
    }

    private static string FindProjectRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager");
            if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public List<CaptureLogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, List<CaptureLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>();
            lock (entries) entries.Add(new(category, logLevel, eventId, properties, formatter(state, exception)));
        }
    }

    private sealed record CaptureLogEntry(string Category, LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> Properties, string Message);

    private sealed class ProviderCommandObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private const string Executed = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";
        private const string Failed = "Microsoft.EntityFrameworkCore.Database.Command.CommandError";
        private const string Cancelled = "Microsoft.EntityFrameworkCore.Database.Command.CommandCanceled";
        private readonly Dictionary<string, ProviderCounts> counts = new(StringComparer.Ordinal);
        private readonly IDisposable allListeners;
        private readonly ConcurrentBag<IDisposable> efEvents = [];

        public ProviderCommandObserver() => allListeners = DiagnosticListener.AllListeners.Subscribe(this);

        public ProviderCounts Get(string contextId)
        {
            lock (counts) return counts.GetValueOrDefault(contextId, new());
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
            {
                efEvents.Add(listener.Subscribe(this, name => name is Executed or Failed or Cancelled));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not CommandEndEventData command || command.Context is not { } context) return;
            lock (counts)
            {
                var current = counts.GetValueOrDefault(context.ContextId.ToString(), new());
                current = current with { DurationTicks = current.DurationTicks + Math.Max(0, command.Duration.Ticks) };
                if (value.Key == Executed && command is CommandExecutedEventData executed)
                {
                    current = executed.ExecuteMethod switch
                    {
                        DbCommandMethod.ExecuteReader => current with { Readers = current.Readers + 1 },
                        DbCommandMethod.ExecuteScalar => current with { Scalars = current.Scalars + 1 },
                        DbCommandMethod.ExecuteNonQuery => current with { NonQueries = current.NonQueries + 1 },
                        _ => current
                    };
                }
                else if (value.Key == Failed) current = current with { Failed = current.Failed + 1 };
                else if (value.Key == Cancelled) current = current with { Cancelled = current.Cancelled + 1 };
                counts[context.ContextId.ToString()] = current;
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            while (efEvents.TryTake(out var subscription)) subscription.Dispose();
            allListeners.Dispose();
        }
    }

    private sealed record ProviderCounts(long Readers = 0, long Scalars = 0, long NonQueries = 0, long Failed = 0, long Cancelled = 0, long DurationTicks = 0);
}

[CollectionDefinition("Debug diagnostic isolation", DisableParallelization = true)]
public sealed class DebugDiagnosticIsolationCollection;
