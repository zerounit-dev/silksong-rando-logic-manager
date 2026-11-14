using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Silksong_Rando_Logic_Manager.Services.HttpLogging;

internal sealed class HttpLogWriter(ApplicationStoragePaths paths) : IHostedService
{
    private readonly object admission = new();
    private readonly Channel<HttpLogEnvelope> queue = Channel.CreateBounded<HttpLogEnvelope>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource abandon = new();
    private readonly long[] dropped = new long[5];
    private bool ready, stopping;
    private Task? worker;
    private int sqliteCode, sqliteExtendedCode;
    private long? lastSummary;

    internal void DropCapture() => Interlocked.Increment(ref dropped[0]);
    internal void Submit(HttpLogEnvelope envelope)
    {
        lock (admission)
        {
            if (stopping) { Interlocked.Increment(ref dropped[4]); return; }
            if (!ready) { Interlocked.Increment(ref dropped[2]); return; }
            // Wait mode is used exclusively with TryWrite. No request waits for
            // capacity, IO, writer readiness, console, retry or recovery.
            if (!queue.Writer.TryWrite(envelope)) Interlocked.Increment(ref dropped[1]);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        worker = Task.Run(RunAsync); // Initialization is not startup/request IO.
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (admission) { stopping = true; queue.Writer.TryComplete(); }
        if (worker is null) return;
        // Host wait is bounded even if synchronous native/OS IO does not return.
        // Only the writer disposes its connection, after its own IO unwinds.
        await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        abandon.Cancel();
    }

    private async Task RunAsync()
    {
        HttpLogStore? store = null;
        long? lastOpenAttempt = null;
        try
        {
            while (!abandon.IsCancellationRequested)
            {
                bool closing;
                lock (admission) closing = stopping;
                if (store is null && !closing && (lastOpenAttempt is null ||
                    Stopwatch.GetElapsedTime(lastOpenAttempt.Value).TotalSeconds >= 30))
                {
                    lastOpenAttempt = Stopwatch.GetTimestamp();
                    try
                    {
                        store = new(Path.Combine(paths.DataRoot, "logs", "logs.db"));
                        lock (admission) ready = !stopping;
                    }
                    catch (Exception exception) { RecordCode(exception); }
                }

                if (queue.Reader.TryRead(out var envelope))
                {
                    if (store is null) Interlocked.Increment(ref dropped[2]);
                    else
                    {
                        try { store.Insert(envelope); }
                        catch (Exception exception)
                        {
                            lock (admission) ready = false;
                            Interlocked.Increment(ref dropped[3]);
                            RecordCode(exception);
                            Close(ref store);
                            // Start the recovery interval at failure, not the last
                            // successful open. Never retry the uncertain envelope.
                            lastOpenAttempt = Stopwatch.GetTimestamp();
                            Drain(2);
                        }
                    }
                    envelope = null; // Release writer-owned full detail before waiting.
                    Summary();
                    continue;
                }

                Summary();
                if (closing) break;
                // A writer-only tick services recovery/diagnostic deadlines even
                // with no accepted records. Cancel each pending channel wait; do
                // not accumulate idle WaitToRead tasks.
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
                tick.CancelAfter(TimeSpan.FromSeconds(1));
                try { await queue.Reader.WaitToReadAsync(tick.Token); }
                catch (OperationCanceledException) when (tick.IsCancellationRequested) { }
            }
        }
        catch (Exception exception)
        {
            // Sink infrastructure faults are isolated from HTTP and host startup.
            RecordCode(exception);
        }
        finally
        {
            lock (admission) ready = false;
            Drain(4);
            Close(ref store);
            Summary();
        }
    }

    private void Drain(int reason) { while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped[reason]); }
    private void Close(ref HttpLogStore? store)
    {
        var owned = store;
        store = null;
        try { owned?.Dispose(); }
        catch (Exception exception) { RecordCode(exception); }
    }
    private void RecordCode(Exception exception)
    {
        if (exception is SqliteException sqlite)
        { sqliteCode = sqlite.SqliteErrorCode; sqliteExtendedCode = sqlite.SqliteExtendedErrorCode; }
        else { sqliteCode = 0; sqliteExtendedCode = 0; }
    }
    private void Summary()
    {
        if (lastSummary is { } last && Stopwatch.GetElapsedTime(last).TotalSeconds < 30) return;
        if (dropped.All(count => count == 0) && sqliteCode == 0 && ready) return;
        lastSummary = Stopwatch.GetTimestamp();
        var counts = new long[dropped.Length];
        for (var i = 0; i < counts.Length; i++) counts[i] = Interlocked.Exchange(ref dropped[i], 0);
        if (counts.All(count => count == 0) && sqliteCode == 0 && ready) return;
        try
        {
            Console.Error.WriteLine($"HTTP logging: capture={counts[0]} full={counts[1]} unavailable={counts[2]} write={counts[3]} shutdown={counts[4]}; ready={ready}; sqlite={sqliteCode}/{sqliteExtendedCode}.");
        }
        catch (Exception) { /* No recursive fallback or payload disclosure. */ }
    }
}
