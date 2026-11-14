using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;
using SilksongRandoLogicManager.Contracts.Diagnostics;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    private string? mapInputStateId;
    public string? ConfirmedMapStateId => mapInputStateId;
    private sealed record MapReadCandidate(MapDataOwner.Entry Entry, string StateId);
    private async Task PrepareRetainedMapAsync()
    {
        if (disposed || mutation || !browser.CanAdoptLocal || mapData.Capture() is null) return;
        var changed = await AdmitMapReadAsync(async (current, token) =>
        {
            using var preparation = OperationTiming.Start("local-map-background-retained-input");
            if (browser.Current?.StateId == mapInputStateId) { preparation.End(TimingOutcome.Skipped); return null; }
            var candidate = await MapInputAsync(current, token, false);
            if (candidate is null || !current()) return null;
            mapData.Accept(candidate.Entry); mapInputStateId = candidate.StateId;
            preparation.End(TimingOutcome.Incorporated);
            return candidate;
        }, () => !disposed, lifetime.Token, observe: true);
        if (changed is not null && !disposed) Changed?.Invoke();
    }
    public Task<LandingAreaMapView?> LoadLandingAsync(Func<bool> owns, CancellationToken token) =>
        LoadLandingAsync(owns, token, PreparationContext.Ordinary);
    internal Task<LandingAreaMapView?> LoadLandingAsync(Func<bool> owns, CancellationToken token, PreparationContext context) =>
        AdmitMapReadAsync((current, cancellation) => ReadLandingMapAsync(current, cancellation, false, context), owns, token, observe: true);
    public async Task<LandingAreaMapView?> RefreshLandingMapAsync(Func<bool> owns, CancellationToken token)
    {
        // This is the Home button's explicit synchronization checkpoint, not a
        // mutation follow-up or a mounted Room document-refresh/adoption path.
        // Never hold catalogue admission while the shared Refresh acquires it.
        if (disposed || !owns()) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        await browser.SynchronizeAsync(linked.Token);
        if (!owns()) return null;
        await browser.RefreshImagesAsync(linked.Token);
        if (!owns()) return null;
        await ReadLocalAsync();
        if (disposed || !owns()) return null;
        // Common facts are installed first. The unchanged local StateId guard and
        // named-query CAS still reject a map source that moved during preparation.
        return await AdmitMapReadAsync((current, cancellation) => ReadLandingMapAsync(current, cancellation, true), owns, linked.Token, observe: true);
    }
    public Task<LandingLinks?> LoadLinksAsync(Guid? room, Guid? scene, Func<bool> owns, CancellationToken token) =>
        AdmitMapReadAsync((current, cancellation) => ReadLinksAsync(room, scene, current, cancellation), owns, token);
    public Task<RoomMapContextView?> LoadRoomMapAsync(Guid room, Func<bool> owns, CancellationToken token) =>
        LoadRoomMapAsync(room, owns, token, PreparationContext.Ordinary);
    internal Task<RoomMapContextView?> LoadRoomMapAsync(Guid room, Func<bool> owns, CancellationToken token, PreparationContext context) =>
        AdmitMapReadAsync((current, cancellation) => ReadRoomMapAsync(room, current, cancellation, false, context), owns, token, observe: true);
    public Task<RoomMapContextView?> RefreshRoomMapAsync(Guid room, Func<bool> owns, CancellationToken token) =>
        RefreshRoomMapCoreAsync(room, owns, token);
    private async Task<RoomMapContextView?> RefreshRoomMapCoreAsync(Guid room, Func<bool> owns, CancellationToken token)
    {
        if (disposed || !owns()) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await browser.SynchronizeAsync(linked.Token);
        if (!owns()) return null;
        await browser.RefreshImagesAsync(linked.Token);
        if (!owns()) return null;
        await ReadLocalAsync();
        return owns() ? await LoadRoomMapAsync(room, owns, linked.Token) : null;
    }

    private async Task<LandingLinks?> ReadLinksAsync(Guid? room, Guid? scene, Func<bool> owns, CancellationToken token)
    {
        var candidate = await MapInputAsync(owns, token, false);
        if (candidate is null) return null;
        var view = LocalMapLinks.Prepare(candidate.Entry.Input, Rooms, room, scene);
        if (!owns() || token.IsCancellationRequested) return null;
        mapData.Accept(candidate.Entry); mapInputStateId = candidate.StateId;
        return view;
    }
    private IReadOnlyDictionary<Guid, ConfirmedRoomFacts>? followSource;
    private ImmutableDictionary<string, Guid?> followTargets = ImmutableDictionary.Create<string, Guid?>(StringComparer.OrdinalIgnoreCase);
    public Task<RoomFollowTarget?> FindFollowRoomAsync(string gameId, Func<bool> owns, CancellationToken token) =>
        AdmitMapReadAsync((current, cancellation) => ReadFollowRoomAsync(gameId, current, cancellation), owns, token);

    private async Task<RoomFollowTarget?> ReadFollowRoomAsync(string gameId, Func<bool> current, CancellationToken token)
    {
        var key = ArchipelagoRoomNames.Normalize(gameId);
        if (key is null) return current() ? new RoomFollowTarget(null) : null;
        var source = Rooms;
        if (!ReferenceEquals(source, followSource))
        {
            var changed = followSource is null || followSource.Count != source.Count;
            var inspected = 0;
            if (!changed)
                foreach (var (id, room) in source)
                {
                    token.ThrowIfCancellationRequested();
                    if (!followSource!.TryGetValue(id, out var old) || old.Header.Observation.IsArchived != room.Header.Observation.IsArchived ||
                        !ArchipelagoRoomNames.Same(old.Header.Values.InGameId, room.Header.Values.InGameId)) { changed = true; break; }
                    if (++inspected % 128 == 0) await browser.YieldAsync(token);
                    if (!current()) return null;
                }
            if (!current()) return null;
            if (!changed)
            {
                followSource = source;
                return new RoomFollowTarget(followTargets.GetValueOrDefault(key));
            }
            var targets = ImmutableDictionary.CreateBuilder<string, Guid?>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var (id, room) in source)
            {
                token.ThrowIfCancellationRequested();
                if (!room.Header.Observation.IsArchived && ArchipelagoRoomNames.Normalize(room.Header.Values.InGameId) is { } name)
                {
                    // Exact existing .NET trim/OrdinalIgnoreCase and unique-active
                    // semantics. Ambiguity is not first/last winner navigation.
                    targets[name] = targets.ContainsKey(name) ? null : id;
                }
                if (++count % 128 == 0) await browser.YieldAsync(token);
                if (!current()) return null;
            }
            if (!current()) return null;
            followTargets = targets.ToImmutable(); followSource = source;
        }
        return current() ? new RoomFollowTarget(followTargets.GetValueOrDefault(key)) : null;
    }

    private async Task<T?> AdmitMapReadAsync<T>(Func<Func<bool>, CancellationToken, Task<T?>> read,
        Func<bool> owns, CancellationToken token, bool observe = false) where T : class
    {
        var ownerEpoch = epoch; long ownerGeneration = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        using var wait = observe ? OperationTiming.Start("local-map-admission-wait") : null;
        await admission.WaitAsync(linked.Token);
        wait?.End();
        bool Current() => Owns(ownerEpoch, ownerGeneration) && !mutation && owns() && !linked.IsCancellationRequested;
        try
        {
            if (disposed || ownerEpoch != epoch || mutation || !owns()) return null;
            ownerGeneration = ++generation;
            return await read(Current, linked.Token);
        }
        catch when (!Owns(ownerEpoch, ownerGeneration) || !owns()) { return null; }
        finally { admission.Release(); }
    }

    private async Task<MapReadCandidate?> MapInputAsync(Func<bool> owns, CancellationToken token, bool refresh,
        PreparationContext context = PreparationContext.Ordinary)
    {
        using var preparation = OperationTiming.Start("local-map-input-preparation");
        var work = new LocalLoadWork();
        if (!owns() || token.IsCancellationRequested || mapData.IsDisposed) return null;
        if (!browser.CanAdoptLocal) return null;
        using var stateCheck = OperationTiming.Start("local-map-state-check");
        var state = await browser.ReadStateAsync(token);
        stateCheck.End(TimingOutcome.Returned);
        if (!owns() || token.IsCancellationRequested || mapData.IsDisposed || !browser.CanAdoptLocal) return null;
        if (state is null) throw new InvalidOperationException("The local map source is unavailable.");
        if (state.StateId != appliedLocalStateId)
            throw new InvalidOperationException("Map preparation no longer matches the admitted common local facts.");
        var previous = mapData.Capture();
        if (state.StateId == mapInputStateId && previous is not null)
        {
            var retained = mapData.Synchronize(Rooms, Statuses, preparation.Id);
            work.Report(preparation, reused: true); preparation.End(TimingOutcome.Returned);
            return new(retained, state.StateId);
        }
        using var query = OperationTiming.Start("local-map-query-return-including-managed-decode");
        var read = await browser.ReadMapsAsync(expected: state, token: token);
        query.End(TimingOutcome.Returned);
        if (!owns() || token.IsCancellationRequested || mapData.IsDisposed || !browser.CanAdoptLocal) return null;
        async Task YieldObserved()
        {
            token.ThrowIfCancellationRequested();
            if (!owns() || mapData.IsDisposed) throw new OperationCanceledException();
            if (context == PreparationContext.Ordinary)
            {
                using (work.MeasureYield()) await browser.YieldAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (!owns() || mapData.IsDisposed) throw new OperationCanceledException();
        }
        using var materialize = OperationTiming.Start("local-map-row-materialization");
        var input = await LocalLogicMaterializer.MapsAsync(read, previous?.Input, YieldObserved, token, work);
        work.Report(materialize, ReferenceEquals(input, previous?.Input)); materialize.End();
        if (!owns() || token.IsCancellationRequested || mapData.IsDisposed) return null;
        var entry = previous is not null && ReferenceEquals(input, previous.Input) ? mapData.Synchronize(Rooms, Statuses, preparation.Id)
            : mapData.Prepare(input, Rooms, Statuses, preparation.Id);
        preparation.End(TimingOutcome.Returned);
        return new(entry, state.StateId);
    }
    private async Task<LandingAreaMapView?> ReadLandingMapAsync(Func<bool> owns, CancellationToken token, bool refresh,
        PreparationContext context = PreparationContext.Ordinary)
    {
        using var load = OperationTiming.Start("local-home-map-demand");
        var candidate = await MapInputAsync(owns, token, refresh, context);
        if (candidate is null) return null;
        var view = mapData.Landing(candidate.Entry, Rooms, Statuses, load.Id);
        if (!owns() || token.IsCancellationRequested || mapData.IsDisposed) return null;
        mapData.Accept(candidate.Entry); mapInputStateId = candidate.StateId;
        load.End(TimingOutcome.Incorporated);
        return view;
    }
    private async Task<RoomMapContextView?> ReadRoomMapAsync(Guid room, Func<bool> owns, CancellationToken token, bool refresh,
        PreparationContext context = PreparationContext.Ordinary)
    {
        using var load = OperationTiming.Start("local-room-map-demand");
        var candidate = await MapInputAsync(owns, token, refresh, context);
        if (candidate is null) return null;
        var view = mapData.Room(candidate.Entry, room, Rooms, Statuses, load.Id);
        if (!owns() || token.IsCancellationRequested || mapData.IsDisposed) return null;
        mapData.Accept(candidate.Entry); mapInputStateId = candidate.StateId;
        load.End(TimingOutcome.Incorporated);
        return view;
    }

    // Local renderer dependency reconciliation only; never acquires data/admission.
    internal AreaMapSurfaceView CurrentMapSurface(AreaMapSurfaceView previous)
    {
        if (disposed || mutation || !browser.CanAdoptLocal || mapInputStateId != appliedLocalStateId
            || mapData.Capture() is null) return previous;
        var entry = mapData.Synchronize(Rooms, Statuses);
        var surface = previous.Context.CurrentRoomId is { } room
            ? mapData.Room(entry, room, Rooms, Statuses).Surface ?? previous
            : mapData.Landing(entry, Rooms, Statuses).Surface ?? previous;
        mapData.Accept(entry);
        return surface;
    }

    public sealed partial class MutationAdmission
    {
        private bool Current(Func<bool> owns) => !store.disposed && !store.RuntimeRetired && store.epoch == owner && store.mutation && owns();
    }
}
