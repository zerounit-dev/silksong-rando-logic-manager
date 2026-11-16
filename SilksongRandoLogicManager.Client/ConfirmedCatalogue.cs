using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Operation-local scheduling; joining a refresh flight never changes its context.
internal enum PreparationContext { Ordinary, Startup }

public sealed record ConfirmedDefinition(VocabularyDefinition Value, ImmutableArray<string> ParsedAliases);
public sealed record ConfirmedVocabulary(ImmutableArray<ImmutableArray<ConfirmedDefinition>> Kinds);
public sealed record ManagerDetail(Guid Id, string Output, string Notes);
public sealed record ConfirmedManager(ConfirmedVocabulary Common, ImmutableArray<ImmutableDictionary<Guid, ManagerDetail>> Details);
public enum InitialLoadingPhase { Neutral, Download, Install, GlobalLookups, Map, Room, FinalPreparation }
public sealed record InitialCatalogueProgress(InitialLoadingPhase Phase, int? CompletedRooms = null, int? TotalRooms = null)
{
    public string Stage => Phase switch
    {
        InitialLoadingPhase.Download => "Downloading the logic database…",
        InitialLoadingPhase.GlobalLookups => "Preparing global lookups…",
        InitialLoadingPhase.Map => "Preparing the map…",
        InitialLoadingPhase.Room => "Opening the room…",
        _ => "Loading Silksong Logic Manager…"
    };
    public string ActivityClass => Phase switch
    {
        InitialLoadingPhase.Download => "load-ui1-download",
        InitialLoadingPhase.Install => "load-ui1-install",
        InitialLoadingPhase.GlobalLookups => "load-ui1-lookups",
        InitialLoadingPhase.Map or InitialLoadingPhase.Room => "load-ui1-app",
        InitialLoadingPhase.FinalPreparation => "load-ui1-final",
        _ => "load-ui1-neutral"
    };
}

/// <summary>The sole application-lifetime confirmed facts and HTTP publication owner.</summary>
public sealed partial class ConfirmedCatalogue(HttpClient http, MapDataOwner mapData, LogicBrowserCache browser, ClientRuntime runtime) : IDisposable
{
    private bool RuntimeRetired => runtime.Retired;
    private LogicBrowserCache Browser => browser;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long epoch, generation;
    private bool disposed, mutation, queued, forceQueued;
    private Task? flight;
    public event Action? Changed;
    public ImmutableDictionary<Guid, ConfirmedRoomFacts> Rooms { get; private set; } = ImmutableDictionary<Guid, ConfirmedRoomFacts>.Empty;
    public RoomEditing.RoomDiagnosticIndex.Facts Diagnostics { get; private set; } = RoomEditing.RoomDiagnosticIndex.Empty;
    public ImmutableArray<CatalogueGroup> Groups { get; private set; } = [];
    public ImmutableDictionary<Guid, RoomStatus> Statuses { get; private set; } = ImmutableDictionary<Guid, RoomStatus>.Empty;
    public ImmutableDictionary<Guid, ImageState> Images { get; private set; } = ImmutableDictionary<Guid, ImageState>.Empty;
    public ConfirmedVocabulary? Vocabulary { get; private set; }
    public ConfirmedManager? Manager { get; private set; }
    public Sidebar Sidebar { get; private set; } = new([], []);
    public ImmutableArray<Guid> PresentationRoomIds { get; private set; } = [];
    public SidebarPresentation? Presentation { get; private set; }
    public SidebarSearchIndex? Search { get; private set; }
    public bool Initialized { get; private set; }
    public bool MutationActive => mutation;
    public bool HasQueuedRefresh => queued;

    public Task RefreshAsync(bool force = false, Func<InitialCatalogueProgress, Task>? initialProgress = null)
        => RefreshAsync(PreparationContext.Ordinary, force, initialProgress);

    internal Task RefreshAsync(PreparationContext context, bool force = false, Func<InitialCatalogueProgress, Task>? initialProgress = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        runtime.RequireActive();
        if (mutation) { queued = true; forceQueued |= force; return Task.CompletedTask; }
        if (flight is { IsCompleted: false }) { queued = true; forceQueued |= force; return flight; }
        force |= forceQueued; queued = forceQueued = false;
        return flight = RefreshBatchAsync(force, initialProgress, context);
    }

    private async Task RefreshBatchAsync(bool force, Func<InitialCatalogueProgress, Task>? initialProgress, PreparationContext context)
    {
        // At most one coalesced follow-up. Continuous external activity cannot loop.
        if (force)
        {
            await browser.SynchronizeAsync(lifetime.Token);
            await browser.RefreshImagesAsync(lifetime.Token);
        }
        await ReadLocalAsync(initialProgress, context);
        if (!disposed && !mutation && queued)
        {
            var nextForce = forceQueued; queued = forceQueued = false;
            if (nextForce)
            {
                await browser.SynchronizeAsync(lifetime.Token);
                await browser.RefreshImagesAsync(lifetime.Token);
            }
            await ReadLocalAsync(context: context);
        }
    }

    private void DeferRefresh(bool force) { queued = true; forceQueued |= force; }

    public async Task MutateAsync(Func<MutationAdmission, Task> operation, CancellationToken token)
    {
        runtime.RequireActive();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mutation) throw new InvalidOperationException("A catalogue mutation is already admitted.");
        mutation = true; var owner = ++epoch; generation++;
        var settled = mutationSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            // Retire before draining; old reads cannot publish while this waits.
            await admission.WaitAsync(linked.Token);
            try { runtime.RequireActive(); await operation(new(this, owner)); }
            finally { admission.Release(); }
        }
        finally
        {
            mutation = false;
            mutationSettlement = null;
            settled.TrySetResult();
            // Background storage may have completed while this admitted feature
            // was installing native/queue state. Drain its deferred local checkpoint
            // after admission releases, without awaiting SQL for feature success.
            if (!disposed && !runtime.Retired && queued && browser.CanAdoptLocal) _ = PrepareBackgroundLocalAsync();
        }
    }

    public sealed partial class MutationAdmission
    {
        private readonly ConfirmedCatalogue store;
        private readonly long owner;
        internal MutationAdmission(ConfirmedCatalogue store, long owner) { this.store = store; this.owner = owner; }
    }

    public void RetireRuntime() { epoch++; generation++; queued = forceQueued = false; }
    private bool Owns(long capturedEpoch, long capturedGeneration) => !disposed && !runtime.Retired && capturedEpoch == epoch && capturedGeneration == generation;
    private static ConfirmedManager PrepareManager(ConfirmedVocabulary common) => new(common,
        common.Kinds.Select(kind => kind.ToImmutableDictionary(x => x.Value.Id,
            x => new ManagerDetail(x.Value.Id, x.Value.Output, x.Value.Notes))).ToImmutableArray());

    private static ConfirmedVocabulary PrepareVocabulary(CatalogueVocabulary value)
    {
        if (value.Token.Kind != DateTimeKind.Utc || value.Token.Ticks == 0) throw new JsonException("Unusable vocabulary timestamp.");
        var kinds = new[] { value.Predicates, value.Crests, value.Methods, value.Items, value.Countables, value.Souls };
        var prepared = ImmutableArray.CreateBuilder<ImmutableArray<ConfirmedDefinition>>();
        foreach (var kind in kinds)
        {
            if (kind is null || kind.Any(x => x is null || x.Id == Guid.Empty || x.Name is null || x.Aliases is null || x.Output is null || x.Notes is null)
                || kind.Select(x => x.Id).Distinct().Count() != kind.Count) throw new JsonException("Unusable vocabulary.");
            prepared.Add(kind.Select(x => new ConfirmedDefinition(x, RequirementCatalogueLanguage.ParseAliases(x.Aliases).ToImmutableArray())).ToImmutableArray());
        }
        if (value.Predicates.Any(x => x.InputSyntax is null)) throw new JsonException("Unusable predicate syntax.");
        return new(prepared.ToImmutable());
    }

    private static ImageState ImageStateFor(ConfirmedRoomFacts room, bool? exists)
    {
        var image = room.Header.SceneImage;
        return room.Header.Observation.IsArchived ? ImageState.None : !image.UseSceneImage ? ImageState.Disabled
            : image.Stale ? ImageState.NeedsRecapture : exists == false ? ImageState.Missing
            : !image.UseUploadedSceneImage && (image.PixelsPerUnitX != 6.35 || image.PixelsPerUnitY != 6.35)
                ? ImageState.NonstandardScale : ImageState.None;
    }

    private static void Unique<T>(IEnumerable<T> values, Func<T, Guid> id)
    {
        var seen = new HashSet<Guid>();
        foreach (var value in values) if (value is null || id(value) == Guid.Empty || !seen.Add(id(value))) throw new JsonException("Duplicate or unusable identity.");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; queued = forceQueued = false; epoch++; generation++; lifetime.Cancel(); lifetime.Dispose();
        if (cacheEventsAttached)
        {
            browser.LocalChanged -= LocalCheckpointChanged;
            browser.ImagesChanged -= LocalImagesChanged;
        }
        mapData.Dispose();
    }
}
