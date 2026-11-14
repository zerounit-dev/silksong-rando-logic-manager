using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Contracts;
using SilksongRandoLogicManager.Contracts.Diagnostics;

namespace SilksongRandoLogicManager.Client;

public sealed record LocalCacheState(string StateId, LogicCacheCheckpoint Checkpoint);
public sealed record CacheRequest(LocalCacheState? From);
public sealed record ReceivedCacheResult<T>(T Result, LogicCacheChanges Cache, CacheRequest Request);
public sealed record LocalCacheReply<T>(long RequestId, T Result);
internal sealed record LocalCacheResponse<T>(bool Success, long RequestId, T Result, DiagnosticSnapshot? Failure, long? BrokerRequestId = null);
public sealed record LocalRoomInventory(Guid Id, string CacheFlagUtc, int CompletionStatus);
public sealed record LocalCacheInventory(LocalCacheState State, IReadOnlyList<LocalRoomInventory> Rooms);
public sealed record CacheTableCount(string Table, string Rows);
public sealed record CacheInspection(LocalCacheState? State, string? LogicalBytes = null,
    IReadOnlyList<CacheTableCount>? Tables = null, string? DomainRows = null);
public sealed record CacheSynchronizationObservation(DateTime ObservedUtc, LogicCacheCheckpoint Checkpoint);
public enum CacheReplacementOutcome { Installed, SharedStateChanged, AdmissionChanged, PublicationsPending }
public sealed class CacheReplacementFailure(bool installationDispatched, Exception inner)
    : Exception("The explicit cache replacement could not be confirmed.", inner)
{
    public bool InstallationDispatched { get; } = installationDispatched;
}

/// <summary>Application-lifetime local SQL/synchronization transport. Shared SQLite
/// owns confirmed persisted facts; this service retains only the last observed opaque
/// checkpoint and RAM-only image evidence, never drafts, roles or commands in SQLite.</summary>
public sealed class LogicBrowserCache(IJSRuntime js, HttpClient http, SessionAccess access, ClientRuntime runtime) : IAsyncDisposable
{
    private const string Header = "X-Logic-Cache";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim synchronization = new(1, 1);
    private Task<IJSObjectReference>? initialization;
    private DotNetObjectReference<LogicBrowserCache>? self;
    private bool disposed;
    private long acceptedObservation;
    private int pendingPublications;
    private bool failedPublication;
    private readonly HashSet<Guid> imageDemands = [];
    // Feature-result provenance outranks older SQL until its own asynchronous
    // installation settles. This gates only RAM adoption, never native success/ACK.
    public bool CanAdoptLocal => !runtime.Retired && pendingPublications == 0 && !failedPublication;
    public LocalCacheState? Current { get; private set; }
    public CacheSynchronizationObservation? LastSuccessfulSynchronization { get; private set; }
    public ImmutableDictionary<Guid, bool> Images { get; private set; } = ImmutableDictionary<Guid, bool>.Empty;
    public event Func<LocalCacheState?, Task>? LocalChanged;
    public event Action? ImagesChanged;
    public event Action<Exception>? Failed;

    private Task<IJSObjectReference> ModuleAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        runtime.RequireActive();
        return initialization ??= InitializeAsync();
    }
    private async Task<IJSObjectReference> InitializeAsync()
    {
        var module = await ApplicationModuleLoader.ImportAsync(js, runtime, "js/logicCache.mjs");
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            self = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("initialize", self, ApplicationBuild.Version, runtime.Installing);
            runtime.RequireActive();
            return module;
        }
        catch (Exception error)
        {
            // Capture before existing cleanup can throw a different interop failure.
            var failure = error is OperationCanceledException ? null : ErrorDiagnostics.Capture(error, "initialize");
            try { self?.Dispose(); self = null; await module.DisposeAsync(); }
            catch (Exception cleanup) when (failure is not null) { throw new LocalCacheFailure(failure, cleanup); }
            if (failure is null) throw;
            throw new LocalCacheFailure(failure, error);
        }
    }
    private bool Observe(LocalCacheState? state, long revision)
    {
        if (disposed || runtime.Retired || revision < acceptedObservation) return false;
        acceptedObservation = revision;
        Current = state;
        return true;
    }
    private async Task<LocalCacheReply<T>> RequestAsync<T>(object request, CancellationToken token, string operation, string query = "Unknown")
    {
        runtime.RequireActive();
        var measured = operation == "query" && query is "catalogue" or "maps";
        using var moduleWork = measured ? OperationTiming.Start("local-named-read-module-availability") : null;
        try
        {
            var module = await ModuleAsync();
            moduleWork?.End();
            using var roundTrip = measured ? OperationTiming.Start(query == "maps"
                ? "local-maps-interop-return-including-managed-decode" : "local-catalogue-interop-return-including-managed-decode") : null;
            var reply = await module.InvokeAsync<LocalCacheResponse<T>>("request", token, request);
            runtime.RequireActive();
            if (roundTrip is not null)
            {
                LocalLoadWork.Request(roundTrip, reply.RequestId, reply.BrokerRequestId, reply.Success);
                roundTrip.End(reply.Success ? TimingOutcome.Returned : TimingOutcome.FailedOrRetired);
            }
            if (!reply.Success) throw new LocalCacheFailure(ErrorDiagnostics.RequestContext(ErrorDiagnostics.FromLocal(reply.Failure), operation, query));
            return new(reply.RequestId, reply.Result);
        }
        catch (OperationCanceledException) { throw; }
        catch (LocalCacheFailure error)
        {
            throw new LocalCacheFailure(ErrorDiagnostics.RequestContext(error.Diagnostic, operation, query), error);
        }
        catch (Exception error) { throw new LocalCacheFailure(ErrorDiagnostics.RequestContext(ErrorDiagnostics.Capture(error, "interop"), operation, query), error); }
    }
    public async Task<LocalCacheState?> ReadStateAsync(CancellationToken token = default)
    {
        var observed = await RequestAsync<LocalCacheState?>(new { operation = "state" }, token, "state");
        Observe(observed.Result, observed.RequestId);
        return observed.Result;
    }
    public async Task<CacheInspection> InspectAsync(CancellationToken token = default)
    {
        var observed = await RequestAsync<CacheInspection>(new { operation = "inspect" }, token, "inspect");
        Observe(observed.Result.State, observed.RequestId);
        return observed.Result;
    }

    // Explicit recovery only: no catch-up/retry, feature notification or command replay.
    // The caller supplies fresh source/feature certification at every destructive boundary.
    public async Task<CacheReplacementOutcome> ReplaceForReloadAsync(Func<bool> admitted, CancellationToken token)
    {
        var dispatched = false;
        try
        {
            using var timeout = SynchronizationTimeout(token);
            await synchronization.WaitAsync(timeout.Token);
            try
            {
                if (!admitted()) return CacheReplacementOutcome.AdmissionChanged;
                if (pendingPublications != 0) return CacheReplacementOutcome.PublicationsPending;
                var prepared = await RequestAsync<LocalCacheState?>(new { operation = "prepare-install" }, timeout.Token, "install");
                var captured = prepared.Result;
                if (!admitted()) return CacheReplacementOutcome.AdmissionChanged;
                if (pendingPublications != 0) return CacheReplacementOutcome.PublicationsPending;
                using var response = await http.GetAsync("/api/v3/cache/database", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != "application/vnd.sqlite3" ||
                    !response.Headers.TryGetValues(Header, out var headers)) throw new JsonException("Missing prepared SQLite metadata.");
                var metadata = JsonSerializer.Deserialize<LogicCacheCheckpoint>(headers.Single(), Json)
                    ?? throw new JsonException("Missing prepared SQLite checkpoint.");
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                runtime.RequireActive();
                if (!admitted()) return CacheReplacementOutcome.AdmissionChanged;
                if (pendingPublications != 0) return CacheReplacementOutcome.PublicationsPending;
                dispatched = true;
                var observed = await RequestAsync<InstallReply>(new { operation = "install", userReplacement = true,
                    expectedStateId = captured?.StateId, checkpoint = metadata, bytes }, timeout.Token, "install");
                Observe(observed.Result.State, observed.RequestId);
                if (observed.Result.Conflict) return CacheReplacementOutcome.SharedStateChanged;
                if (observed.Result.State is null || !observed.Result.Changed) throw new JsonException("Missing replacement acknowledgement.");
                if (pendingPublications == 0) failedPublication = false;
                return CacheReplacementOutcome.Installed;
            }
            finally { synchronization.Release(); }
        }
        catch (Exception error) { throw new CacheReplacementFailure(dispatched, error); }
    }
    internal async Task<LocalVocabularySource> ReadVocabularyAsync(LocalCacheState? expected = null, CancellationToken token = default)
    {
        var observed = await RequestAsync<JsonElement>(new { operation = "query", name = "vocabulary", expectedStateId = expected?.StateId }, token, "query", "vocabulary");
        var (state, conflict) = LocalNamedReadReader.Header(observed.Result);
        Observe(state, observed.RequestId);
        if (conflict || state is null)
            throw new InvalidOperationException("The local confirmed facts changed during preparation. Read the current local checkpoint before trying again.");
        return LocalNamedReadReader.Vocabulary(observed.Result);
    }
    internal async Task<LocalMapsSource> ReadMapsAsync(LocalCacheState? expected = null, CancellationToken token = default)
    {
        var observed = await RequestAsync<JsonElement>(new { operation = "query", name = "maps", expectedStateId = expected?.StateId }, token, "query", "maps");
        var (state, conflict) = LocalNamedReadReader.Header(observed.Result);
        Observe(state, observed.RequestId);
        if (conflict || state is null)
            throw new InvalidOperationException("The local confirmed facts changed during preparation. Read the current local checkpoint before trying again.");
        return LocalNamedReadReader.Maps(observed.Result);
    }
    internal async Task<LocalRoomSource> ReadRoomAsync(Guid roomId, LocalCacheState? expected = null, CancellationToken token = default)
    {
        var observed = await RequestAsync<JsonElement>(new { operation = "query", name = "room", roomId, expectedStateId = expected?.StateId }, token, "query", "room");
        var (state, conflict) = LocalNamedReadReader.Header(observed.Result);
        Observe(state, observed.RequestId);
        if (conflict || state is null)
            throw new InvalidOperationException("The local confirmed facts changed during preparation. Read the current local checkpoint before trying again.");
        return LocalNamedReadReader.Room(observed.Result, roomId);
    }
    private sealed record InstallReply(bool Conflict, LocalCacheState? State, bool Changed = false);
    internal async Task<LocalCatalogueSource> ReadCatalogueAsync(IReadOnlyDictionary<Guid, ConfirmedRoomFacts> known,
        CancellationToken token = default)
    {
        using var observationsWork = OperationTiming.Start("local-catalogue-request-observation-preparation");
        var observations = known.Select(x => new { id = x.Key, cacheFlagUtc = x.Value.Observation.CacheFlagUtc.Ticks.ToString(CultureInfo.InvariantCulture) }).ToArray();
        new LocalLoadWork().Report(observationsWork, items: observations.Length);
        observationsWork.End();
        var observed = await RequestAsync<JsonElement>(new { operation = "query", name = "catalogue", knownRooms = observations }, token, "query", "catalogue");
        var (state, conflict) = LocalNamedReadReader.Header(observed.Result);
        Observe(state, observed.RequestId);
        if (conflict || state is null)
            throw new InvalidOperationException("The local common catalogue is unavailable.");
        return LocalNamedReadReader.Catalogue(observed.Result, state);
    }

    public async Task<LocalCacheInventory> ReadInventoryAsync(LocalCacheState? expected = null, CancellationToken token = default)
    {
        var observed = await RequestAsync<JsonElement>(new { operation = "query", name = "inventory", expectedStateId = expected?.StateId }, token, "query", "inventory");
        var (state, conflict) = LocalNamedReadReader.Header(observed.Result);
        Observe(state, observed.RequestId);
        if (conflict || state is null)
            throw new InvalidOperationException("The local Room inventory changed during preparation. Read the current local checkpoint before trying again.");
        return new(state, LocalNamedReadReader.Inventory(observed.Result));
    }

    public async Task YieldAsync(CancellationToken token = default) =>
        await (await ModuleAsync()).InvokeVoidAsync("yieldTask", token);

    // Capture from already-observed RAM, never worker/SQL before an authoring send.
    // Missing/stale knowledge simply asks complete catch-up or a later binary install.
    public CacheRequest Bind(HttpRequestMessage request)
    {
        runtime.RequireActive();
        var captured = new CacheRequest(Current);
        if (captured.From is { } known) request.Headers.Add(Header, JsonSerializer.Serialize(known.Checkpoint, Json));
        return captured;
    }
    public static async Task<ReceivedCacheResult<T>> DecodeAsync<T>(HttpResponseMessage response,
        CacheRequest request, CancellationToken token = default)
    {
        var envelope = await response.Content.ReadFromJsonAsync<CacheResult<T>>(Json, token)
            ?? throw new JsonException("Missing complete cache result envelope.");
        return new(envelope.Result, envelope.Cache, request);
    }
    // Invoke only AFTER original native/domain/queue installation and ACK accounting.
    // The background task cannot convert a committed domain result into a rollback.
    public void Publish<T>(ReceivedCacheResult<T> received)
    {
        if (disposed || runtime.Retired) return;
        pendingPublications++;
        _ = PublishAsync(received.Cache, received.Request.From);
    }
    private async Task PublishAsync(LogicCacheChanges changes, LocalCacheState? captured)
    {
        LocalCacheState? installed = null;
        try
        {
            using var timeout = SynchronizationTimeout(default);
            await synchronization.WaitAsync(timeout.Token);
            try { installed = await ApplyAsync(changes, captured, timeout.Token); }
            finally { synchronization.Release(); }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) { failedPublication = true; if (!disposed && !runtime.Retired) Failed?.Invoke(error); }
        finally { pendingPublications--; }
        if (!disposed && !runtime.Retired && CanAdoptLocal && installed is not null)
        {
            try { await NotifyAsync(installed); }
            catch (Exception error) { if (!disposed) Failed?.Invoke(error); }
        }
    }
    private CancellationTokenSource SynchronizationTimeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        return timeout;
    }
    public async Task<LocalCacheState> StartupAsync(CancellationToken token = default,
        Func<InitialCatalogueProgress, Task>? initialProgress = null)
    {
        try { return await StartupCoreAsync(token, initialProgress); }
        catch (Exception error) { access.FailInitial(error); throw; }
    }
    public async Task InstallCurrentRuntimeAsync(CancellationToken token = default, Func<InitialCatalogueProgress, Task>? progress = null)
    {
        runtime.RequireActive();
        using var timeout = SynchronizationTimeout(token);
        await synchronization.WaitAsync(timeout.Token);
        try
        {
            var prepared = await RequestAsync<LocalCacheState?>(new { operation = "prepare-install", currentRuntimeInstallation = true }, timeout.Token, "install");
            using var startupResponse = await http.GetAsync("/api/v3/cache/startup", timeout.Token);
            startupResponse.EnsureSuccessStatusCode();
            var startup = await startupResponse.Content.ReadFromJsonAsync<LogicCacheStartup>(Json, timeout.Token)
                ?? throw new JsonException("Missing combined startup response.");
            runtime.RequireActive(); access.Accept(startup.Session);
            if (startup.Cache is null) throw new JsonException("Missing protected startup cache result.");
            if (progress is not null) await progress(new(InitialLoadingPhase.Download));
            using var response = await http.GetAsync("/api/v3/cache/database", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/vnd.sqlite3" || !response.Headers.TryGetValues(Header, out var headers))
                throw new JsonException("Missing prepared SQLite metadata.");
            var checkpoint = JsonSerializer.Deserialize<LogicCacheCheckpoint>(headers.Single(), Json) ?? throw new JsonException("Missing checkpoint.");
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            runtime.RequireActive();
            if (progress is not null) await progress(new(InitialLoadingPhase.Install));
            var reply = await RequestAsync<InstallReply>(new { operation = "install", currentRuntimeInstallation = true,
                userReplacement = true, expectedStateId = prepared.Result?.StateId, checkpoint, bytes }, timeout.Token, "install");
            runtime.RequireActive();
            if (reply.Result.Conflict || !reply.Result.Changed || reply.Result.State is null) throw new InvalidOperationException("Installation ownership or shared state changed.");
            Observe(reply.Result.State, reply.RequestId);
            ObserveImages(startup.Images);
            LastSuccessfulSynchronization = new(DateTime.UtcNow, reply.Result.State.Checkpoint);
            await runtime.FinishInstallationAsync();
            await (await ModuleAsync()).InvokeVoidAsync("installationComplete");
        }
        catch (Exception error) { if (!runtime.Retired && error is not SessionAccessException) runtime.InstallationFailed(); throw; }
        finally { synchronization.Release(); }
    }
    private async Task<LocalCacheState> StartupCoreAsync(CancellationToken token,
        Func<InitialCatalogueProgress, Task>? initialProgress)
    {
        using var timeout = SynchronizationTimeout(token);
        LocalCacheState installed;
        await synchronization.WaitAsync(timeout.Token);
        try
        {
            var captured = await ReadStateAsync(timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/cache/startup");
            if (captured is not null) request.Headers.Add(Header, JsonSerializer.Serialize(captured.Checkpoint, Json));
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            var startup = await response.Content.ReadFromJsonAsync<LogicCacheStartup>(Json, timeout.Token)
                ?? throw new JsonException("Missing combined startup response.");
            runtime.RequireActive();
            access.Accept(startup.Session);
            if (startup.Cache is null) throw new JsonException("Missing protected startup cache result.");
            ObserveImages(startup.Images);
            installed = await ApplyAsync(startup.Cache, captured, timeout.Token, initialProgress);
            LastSuccessfulSynchronization = new(DateTime.UtcNow, installed.Checkpoint);
        }
        finally { synchronization.Release(); }
        await NotifyAsync(installed);
        return installed;
    }
    public async Task<LocalCacheState> SynchronizeAsync(CancellationToken token = default)
    {
        using var timeout = SynchronizationTimeout(token);
        LocalCacheState installed;
        await synchronization.WaitAsync(timeout.Token);
        try
        {
            var captured = await ReadStateAsync(timeout.Token);
            installed = await ApplyAsync(await ChangesAsync(captured, timeout.Token), captured, timeout.Token);
            LastSuccessfulSynchronization = new(DateTime.UtcNow, installed.Checkpoint);
            // Explicit coherent catch-up recovers failed disposable storage, not a
            // business operation. A new still-pending own result keeps precedence.
            if (pendingPublications == 0) failedPublication = false;
        }
        finally { synchronization.Release(); }
        if (CanAdoptLocal) await NotifyAsync(installed);
        return installed;
    }
    private async Task<LogicCacheChanges> ChangesAsync(LocalCacheState? from, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync("/api/v3/cache/changes", new LogicCacheCheck(from?.Checkpoint), Json, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LogicCacheChanges>(Json, token)
            ?? throw new JsonException("Missing complete cache changes response.");
    }
    private async Task<LocalCacheState> ApplyAsync(LogicCacheChanges changes, LocalCacheState? captured, CancellationToken token,
        Func<InitialCatalogueProgress, Task>? initialProgress = null)
    {
        // Preserve initialization before any database-image HTTP read, including failures.
        try { await ModuleAsync(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new LocalCacheFailure(ErrorDiagnostics.RequestContext(ErrorDiagnostics.Capture(error, "interop"),
                changes.RequiresDatabase ? "install" : "patch", "Unknown"), error);
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            LocalCacheReply<InstallReply> observed;
            if (changes.RequiresDatabase)
            {
                var preparation = await RequestAsync<LocalCacheState?>(new { operation = "prepare-install" }, token, "install");
                captured = preparation.Result;
                if (initialProgress is not null) await initialProgress(new(InitialLoadingPhase.Download));
                using var response = await http.GetAsync("/api/v3/cache/database", HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != "application/vnd.sqlite3" ||
                    !response.Headers.TryGetValues(Header, out var headers)) throw new JsonException("Missing prepared SQLite metadata.");
                var metadata = JsonSerializer.Deserialize<LogicCacheCheckpoint>(headers.Single(), Json)
                    ?? throw new JsonException("Missing prepared SQLite checkpoint.");
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                runtime.RequireActive();
                if (initialProgress is not null) await initialProgress(new(InitialLoadingPhase.Install));
                observed = await RequestAsync<InstallReply>(new { operation = "install", expectedStateId = captured?.StateId, checkpoint = metadata, bytes }, token, "install");
            }
            else observed = await RequestAsync<InstallReply>(new { operation = "patch", expectedStateId = captured?.StateId, changes }, token, "patch");
            var installed = observed.Result;
            Observe(installed.State, observed.RequestId);
            if (!installed.Conflict)
            {
                var state = installed.State ?? throw new JsonException("Missing installed cache checkpoint.");
                return state;
            }
            // Only a real StateId conflict rebases this already-admitted cache
            // synchronization. The original business request is NEVER sent again.
            // The same 120s cancellation bounds repeated genuine CAS contention.
            if (initialProgress is not null) await initialProgress(new(InitialLoadingPhase.Neutral));
            captured = installed.State;
            changes = await ChangesAsync(captured, token);
        }
    }
    public void ObserveImages(IReadOnlyList<LogicCacheImageObservation> observations)
    {
        if (disposed || runtime.Retired) return;
        var next = Images;
        foreach (var observation in observations) next = next.SetItem(observation.RoomId, observation.ImageExists);
        if (ReferenceEquals(next, Images)) return;
        Images = next;
        ImagesChanged?.Invoke();
    }
    public async Task ObserveImagesAsync(IReadOnlyList<Guid> roomIds, CancellationToken token = default)
    {
        using var response = await http.PostAsJsonAsync("/api/v3/cache/image-observations", new LogicCacheImageRequest(roomIds), Json, token);
        response.EnsureSuccessStatusCode();
        ObserveImages(await response.Content.ReadFromJsonAsync<IReadOnlyList<LogicCacheImageObservation>>(Json, token)
            ?? throw new JsonException("Missing raw image observations."));
    }
    public async Task RefreshImagesAsync(CancellationToken token = default)
    {
        var inventory = await ReadInventoryAsync(token: token);
        await ObserveImagesAsync(inventory.Rooms.Select(x => x.Id).ToArray(), token);
    }
    public void DemandImage(Guid roomId)
    {
        if (disposed || runtime.Retired || Images.ContainsKey(roomId) || !imageDemands.Add(roomId)) return;
        _ = DemandImageAsync(roomId);
    }
    private async Task DemandImageAsync(Guid roomId)
    {
        try
        {
            // Warm Room geometry is already usable. Bind ordinary live access
            // before this image-only read; startup may supply the evidence first.
            await access.InitialSession.WaitAsync(lifetime.Token);
            if (!disposed && !runtime.Retired && !Images.ContainsKey(roomId)) await ObserveImagesAsync([roomId], lifetime.Token);
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) { if (!disposed) Failed?.Invoke(error); }
        finally { imageDemands.Remove(roomId); }
    }
    private async Task NotifyAsync(LocalCacheState? state)
    {
        if (disposed || runtime.Retired) return;
        if (LocalChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<LocalCacheState?, Task>>())
        { if (disposed || runtime.Retired) return; await handler(state); }
    }
    [JSInvokable]
    public async Task LocalCacheVisible(LocalCacheReply<LocalCacheState?> observed)
    {
        // A guide/map query may already have observed this StateId without installing
        // common RAM facts. That transport observation is not the consumer's applied
        // witness. Each consumer compares its own installed checkpoint before work.
        if (!Observe(observed.Result, observed.RequestId)) return;
        await NotifyAsync(observed.Result);
    }
    [JSInvokable]
    public void LocalCacheUnavailable(DiagnosticSnapshot? failure)
    {
        if (!disposed && !runtime.Retired) Failed?.Invoke(new LocalCacheFailure(ErrorDiagnostics.FromLocal(failure)));
    }
    [JSInvokable]
    public void LocalCacheReleaseRejected() => runtime.Retire();
    public void RetireRuntime()
    {
        if (initialization is { IsCompletedSuccessfully: true } && initialization.Result is IJSInProcessObjectReference native)
            native.InvokeVoid("retireRuntime");
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try
        {
            if (initialization is not null)
            {
                try
                {
                    var module = await initialization;
                    await module.InvokeVoidAsync("dispose");
                    await module.DisposeAsync();
                }
                catch (JSDisconnectedException) { }
                catch (ObjectDisposedException) { }
            }
        }
        finally { self?.Dispose(); lifetime.Dispose(); }
    }
}
