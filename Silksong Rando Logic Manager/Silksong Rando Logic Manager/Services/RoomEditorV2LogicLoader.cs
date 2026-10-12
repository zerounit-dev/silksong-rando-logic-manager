using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public interface IRoomEditorV2LogicLoader { Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken); }
/// <summary>Coordinator-only whole-room batch seam. The production implementation keeps logic and scene reads in one SQLite snapshot.</summary>
public interface IRoomEditorV2SceneBatchLoader
{
    Task<RoomEditorV2View?> LoadAsync(Guid roomId, bool includeScene, CancellationToken cancellationToken);
}
/// <summary>Internal coordinator seam; components never obtain this scene source.</summary>
public interface IRoomEditorV2SceneSource { ISceneLayoutLoader? SceneLoader { get; } }

/// <summary>Phase-1's single snapshot-consistent, scalar-only room logic batch.</summary>
public sealed class RoomEditorV2LogicLoader(IDbContextFactory<LogicDbContext> contexts, ISceneLayoutLoader? sceneLoader = null,
    AppliedRoomStatusService? appliedStatuses = null) : IRoomEditorV2LogicLoader, IRoomEditorV2SceneSource, IRoomEditorV2SceneBatchLoader
{
    private readonly AppliedRoomStatusService statusService = appliedStatuses ?? new(contexts);
    ISceneLayoutLoader? IRoomEditorV2SceneSource.SceneLoader => sceneLoader;
    public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await db.Database.BeginTransactionAsync(cancellationToken);
        return await LoadAsync(db, roomId, false, cancellationToken);
    }
    public async Task<RoomEditorV2View?> LoadAsync(Guid roomId, bool includeScene, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await db.Database.BeginTransactionAsync(cancellationToken);
        return await LoadAsync(db, roomId, includeScene, cancellationToken);
    }
    private async Task<RoomEditorV2View?> LoadAsync(LogicDbContext db, Guid roomId, bool includeScene, CancellationToken cancellationToken)
    {
        var header = await RoomHeaderLoader.LoadAsync(db, roomId, cancellationToken);
        if (header is null) return null;

        var suggestions = await RoomSuggestionLoader.LoadAsync(db, cancellationToken);
        var subrooms = await SubroomLoader.LoadAsync(db, roomId, cancellationToken);
        var transitions = await TransitionLoader.LoadAsync(db, roomId, cancellationToken);
        var connections = await ConnectionLoader.LoadAsync(db, roomId, cancellationToken);
        var checks = await CheckLoader.LoadAsync(db, roomId, cancellationToken);
        var roomCandidates = await RoomReferenceCandidateLoader.LoadAsync(db, transitions, cancellationToken);
        var transitionFacts = await TransitionValidationLoader.LoadAsync(db, roomId, transitions, cancellationToken);
        var checkFacts = await CheckValidationLoader.LoadAsync(db, cancellationToken);
        var predicates = await RequirementsParserPredicateLoader.LoadAsync(db, cancellationToken);
        var items = await RequirementsParserItemLoader.LoadAsync(db, cancellationToken);
        var countables = await db.RequirementCountables.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementsParserCountableView(RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToListAsync(cancellationToken);
        var enemies = await db.RequirementEnemyTypes.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementsParserEnemyView(RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToListAsync(cancellationToken);
        var crests = await db.RequirementCrests.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementsParserCrestView(RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToListAsync(cancellationToken);
        var view = RoomEditorV2Mapper.Map(header, suggestions, roomCandidates, subrooms, transitions, connections, checks, transitionFacts, checkFacts);
        var status = await statusService.LoadAsync(db, [roomId], cancellationToken);
        view = view with { Header = view.Header with { AppliedStatus = status.GetValueOrDefault(roomId) } };
        view = view with
        {
            RequirementsParserContext = new(
                suggestions.Select(x => new RequirementsParserRoomView(x.Id, x.ReferenceId)).ToArray(),
                checkFacts.NameConflicts.Select(x => new RequirementsParserCheckView(x.RoomId, x.FriendlyName)).ToArray(),
                predicates,
                items, countables, enemies, crests)
        };
        return includeScene && sceneLoader is not null
            ? view with { Scene = await sceneLoader.LoadAsync(db, roomId, cancellationToken) }
            : view;
    }
}
internal static class RequirementsParserPredicateLoader
{
    internal static Task<List<RequirementsParserPredicateView>> LoadAsync(LogicDbContext db, CancellationToken token) =>
        db.RequirementPredicates.AsNoTracking()
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementsParserPredicateView(x.InputSyntax, RequirementCatalogueLanguage.ParseAliases(x.Aliases)))
            .ToListAsync(token);
}
internal static class RequirementsParserItemLoader
{
    internal static Task<List<RequirementsParserItemView>> LoadAsync(LogicDbContext db, CancellationToken token) =>
        db.RequirementItems.AsNoTracking()
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementsParserItemView(RequirementCatalogueLanguage.ParseAliases(x.Aliases)))
            .ToListAsync(token);
}

/// <summary>Coordinator-only timing captured when a complete logical view applies.</summary>
public sealed record V2AppliedRefreshDiagnostic(long Sequence, TimeSpan ServerElapsed);
/// <summary>Completion of a current-room recheck, including whether its refresh applied to this page.</summary>
public sealed record RoomRequirementRecheckCompletion(RequirementValidationPreflightResult Result, bool RefreshApplied);

/// <summary>The SQLite command committed; only its required presentation refresh failed.</summary>
public sealed class ChildPostCommitRefreshException<TOutcome>(TOutcome committed, Exception cause)
    : Exception("The child command committed, but the complete room refresh failed.", cause)
{
    public TOutcome Committed { get; } = committed;
}

public sealed class RoomEditorV2RefreshCoordinator(IRoomEditorV2LogicLoader loader, ISceneLayoutLoader? sceneLoader = null, TimeProvider? timeProvider = null, Action? sidebarInvalidated = null) : IDisposable
{
    private CancellationTokenSource? loading; private long iterator; private long appliedRefreshSequence; private Guid currentRoomId; private bool disposed;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public RoomEditorV2View? View { get; private set; } public Guid CurrentRoomId => currentRoomId;
    private Guid? checkRecoveryRoom;
    public bool RequiresCheckRoomReload => checkRecoveryRoom == currentRoomId;
    public async Task<bool> ReloadCheckRoomAsync(Guid roomId)
    {
        var before=LastAppliedRefresh?.Sequence;
        var owner=iterator;
        var missing = await RefreshAsync(roomId, loadScene: true);
        if(!disposed && currentRoomId == roomId && iterator == owner + 1 && (missing || LastAppliedRefresh?.Sequence != before))checkRecoveryRoom=null;
        return missing;
    }
    private int activeLoads;
    public bool IsLoading => Volatile.Read(ref activeLoads) > 0;
    /// <summary>Page-local route/refresh generation used to reject completion-bound stale command results.</summary>
    public long RefreshGeneration => iterator;
    /// <summary>Transient information for the last complete view actually applied to this page.</summary>
    public V2AppliedRefreshDiagnostic? LastAppliedRefresh { get; private set; }
    /// <summary>Test-visible V2-boundary trace. Map and scene loaders are deliberately not dependencies here.</summary>
    public V2RoomOperationTrace OperationTrace { get; } = new();
    public async Task<bool> RefreshAsync(Guid roomId, bool loadScene = true)
    {
        if (disposed) return false;
        Interlocked.Increment(ref activeLoads);
        try
        {
        if (currentRoomId != roomId) InvalidateRouteState();
        loading?.Cancel(); loading?.Dispose(); var cancellation = loading = new(); var refresh = ++iterator;
        if (currentRoomId != roomId) { View = null; checkRecoveryRoom = null; }
        currentRoomId = roomId;
        var started = clock.GetTimestamp();
        var useBatch = loadScene && loader is IRoomEditorV2SceneBatchLoader;
        var fresh = useBatch
            ? await ((IRoomEditorV2SceneBatchLoader)loader).LoadAsync(roomId, true, cancellation.Token)
            : await loader.LoadAsync(roomId, cancellation.Token);
        if (cancellation.IsCancellationRequested || refresh != iterator || currentRoomId != roomId) return false;
        if (fresh is null) { View = null; return true; }
        var effectiveSceneLoader = sceneLoader ?? (loader as IRoomEditorV2SceneSource)?.SceneLoader;
        if (loadScene && !useBatch && effectiveSceneLoader is not null)
        {
            var scene = await effectiveSceneLoader.LoadAsync(roomId, cancellation.Token);
            if (cancellation.IsCancellationRequested || refresh != iterator || currentRoomId != roomId) return false;
            fresh = fresh with { Scene = scene };
            OperationTrace.RecordSceneLoad("scene-load");
        }
        else if (loadScene && useBatch && effectiveSceneLoader is not null)
            OperationTrace.RecordSceneLoad("scene-load");
        // A deliberate same-room scene skip leaves Scene absent from the fresh
        // logic batch. That absence is not a scene-clear instruction: the scene
        // loader is the sole authority for replacing (including clearing) it.
        View = RoomEditorV2Mapper.Reconcile(View, fresh, retainCurrentScene: !loadScene);
        LastAppliedRefresh = new(++appliedRefreshSequence, clock.GetElapsedTime(started));
        return false;
        }
        finally { Interlocked.Decrement(ref activeLoads); }
    }
    public async Task<V2SubroomCommandOutcome> CommitSubroomCommandAsync(Guid roomId, string command, Func<Task<V2SubroomCommandOutcome>> execute, bool sceneRelevant = false)
    {
        if(RequiresCheckRoomReload)return new(V2SubroomCommandStatus.ExpectedFailure,"Reload room before editing.");
        return await CommitChildCommandAsync(roomId, command, execute, result => result.Status == V2SubroomCommandStatus.Committed,
            (result, applied, reload) => result with { PresentationRefreshed = applied, RequiresRoomReload = reload }, sceneRelevant);
    }
    public async Task<V2CheckCommandOutcome> CommitCheckCommandAsync(Guid roomId, string command, Func<Task<V2CheckCommandOutcome>> execute, bool sceneRelevant = false)
    {
        if(RequiresCheckRoomReload)return new(V2CheckCommandStatus.ExpectedFailure,"Reload room before editing.");
        return await CommitChildCommandAsync(roomId, command, execute, result => result.Status == V2CheckCommandStatus.Committed,
            (result, applied, reload) => result with { PresentationRefreshed = applied, RequiresRoomReload = reload }, sceneRelevant);
    }
    public async Task<V2TransitionCommandOutcome> CommitTransitionCommandAsync(Guid roomId, string command, Func<Task<V2TransitionCommandOutcome>> execute, bool sceneRelevant = false)
    {
        if(RequiresCheckRoomReload)return new(V2TransitionCommandStatus.ExpectedFailure,"Reload room before editing.");
        return await CommitChildCommandAsync(roomId, command, execute, result => result.Status == V2TransitionCommandStatus.Committed,
            (result, applied, reload) => result with { PresentationRefreshed = applied, RequiresRoomReload = reload }, sceneRelevant);
    }
    public async Task<V2ConnectionCommandOutcome> CommitConnectionCommandAsync(Guid roomId, string command, Func<Task<V2ConnectionCommandOutcome>> execute, bool sceneRelevant = false)
    {
        if(RequiresCheckRoomReload)return new(V2ConnectionCommandStatus.ExpectedFailure,"Reload room before editing.");
        return await CommitChildCommandAsync(roomId, command, execute, result => result.Status == V2ConnectionCommandStatus.Committed,
            (result, applied, reload) => result with { PresentationRefreshed = applied, RequiresRoomReload = reload }, sceneRelevant,
            result => result.AnnotationRefreshImpact == V2AnnotationRefreshImpact.CanvasAndViewport);
    }
    private async Task<TOutcome> CommitChildCommandAsync<TOutcome>(Guid roomId, string command,
        Func<Task<TOutcome>> execute, Func<TOutcome, bool> committed,
        Func<TOutcome, bool, bool, TOutcome> completion, bool sceneRelevant, Func<TOutcome, bool>? resultSceneRelevant = null)
    {
        var owner = iterator;
        var result = completion(await execute(), false, false);
        if (!committed(result) || disposed || owner != iterator || currentRoomId != roomId) return result;
        OperationTrace.RecordCommitted(command);
        bool applied;
        try { applied = await RefreshAfterCommandAsync(roomId, sceneRelevant || resultSceneRelevant?.Invoke(result) == true); }
        catch (Exception cause)
        {
            if (disposed || currentRoomId != roomId || iterator != owner + 1) return result;
            // One room recovery lock for every child outcome, not competing modes.
            checkRecoveryRoom = roomId;
            throw new ChildPostCommitRefreshException<TOutcome>(completion(result, false, true), cause);
        }
        if (disposed || currentRoomId != roomId || iterator != owner + 1 || !applied) return result;
        OperationTrace.RecordCompletedRefresh(command);
        return completion(result, true, false);
    }
    public async Task<V2RoomHeaderCommandOutcome> CommitRoomHeaderCommandAsync(Guid roomId, string command, Func<Task<V2RoomHeaderCommandOutcome>> execute, bool sceneRelevant = false)
    {
        if(RequiresCheckRoomReload)return new(V2RoomHeaderCommandStatus.Unchanged,"Reload room before editing.");
        var owner=iterator;
        var result = await execute();
        if (result.Status != V2RoomHeaderCommandStatus.Committed||disposed||iterator!=owner||currentRoomId!=roomId) return result;
        OperationTrace.RecordCommitted(command);
        await RefreshAfterCommandAsync(roomId, sceneRelevant);
        OperationTrace.RecordCompletedRefresh(command);
        return result;
    }
    public async Task<V2SceneImageCaptureCommandOutcome> CommitSceneImageCommandAsync(Guid roomId, string command, Func<Task<V2SceneImageCaptureCommandOutcome>> execute)
    {
        if(RequiresCheckRoomReload)return new(V2SceneImageCaptureCommandStatus.ExpectedFailure,"Reload room before editing.");
        return await CommitChildCommandAsync(roomId, command, execute, result => result.Status == V2SceneImageCaptureCommandStatus.Committed,
            (result, applied, reload) => result with { PresentationRefreshed = applied, RequiresRoomReload = reload }, sceneRelevant: true);
    }
    public async Task<V2RoomLifecycleCommandOutcome> CommitRoomLifecycleCommandAsync(Guid roomId, string command, Func<Task<V2RoomLifecycleCommandOutcome>> execute, bool sceneRelevant = false)
    {
        if(RequiresCheckRoomReload)return new(V2RoomLifecycleCommandStatus.ExpectedFailure,"Reload room before editing.");
        var owner=iterator;
        var result = await execute();
        if (result.Status != V2RoomLifecycleCommandStatus.Committed||disposed||iterator!=owner||currentRoomId!=roomId) return result;
        OperationTrace.RecordCommitted(command);
        await RefreshAfterCommandAsync(roomId, sceneRelevant);
        OperationTrace.RecordCompletedRefresh(command);
        return result;
    }
    public async Task<RoomRequirementRecheckCompletion> CommitRequirementRecheckAsync(Guid roomId, Func<Task<RequirementValidationPreflightResult>> execute)
    {
        var commandIterator = iterator;
        var result = await execute();
        if (disposed || commandIterator != iterator || (currentRoomId != Guid.Empty && currentRoomId != roomId)) return new(result, false);
        OperationTrace.RecordCommitted("room-requirements-recheck");
        var refreshApplied = await RefreshAfterCommandAsync(roomId, false);
        if (refreshApplied) OperationTrace.RecordCompletedRefresh("room-requirements-recheck");
        return new(result, refreshApplied);
    }
    private async Task<bool> RefreshAfterCommandAsync(Guid roomId, bool loadScene)
    {
        var before = LastAppliedRefresh?.Sequence;
        var expected = iterator + 1;
        await RefreshAsync(roomId, loadScene);
        var applied = !disposed && currentRoomId == roomId && iterator == expected && LastAppliedRefresh?.Sequence != before;
        if (applied) sidebarInvalidated?.Invoke();
        return applied;
    }
    /// <summary>Invalidates the current route's stale-result guard.</summary>
    public void InvalidateRouteState() { ++iterator; }
    public void Dispose() { disposed = true; InvalidateRouteState(); loading?.Cancel(); loading?.Dispose(); }
}

/// <summary>Invocation-only evidence for a V2 committed room operation.</summary>
public sealed class V2RoomOperationTrace
{
    private readonly List<string> events = [];
    public IReadOnlyList<string> Events => events;
    public int MapLoaderInvocations { get; private set; }
    public int SceneLoaderInvocations { get; private set; }
    internal void RecordCommitted(string command) => Record($"{command}:committed");
    internal void RecordCompletedRefresh(string command) => Record($"{command}:complete-room-refresh");
    internal void RecordSceneLoad(string command) { SceneLoaderInvocations++; Record($"{command}:scene-load"); }
    private void Record(string entry) => events.Add(entry);
}

/// <summary>Exhaustive Phase-4 classification of already-delivered V2 durable inputs.
/// It deliberately receives drafts/baselines, not route names, so a route cannot
/// accidentally request scene work for a matrix-irrelevant field.</summary>
public static class SceneRefreshImpact
{
    // These members are the page/coordinator's only classification authority for
    // delivered durable routes.  Do not replace them with route-local flags.
    public static bool SubroomSave(SubroomDurableBaseline baseline, SubroomDraft draft) => baseline.FriendlyName != draft.FriendlyName || baseline.ReferenceId != draft.ReferenceId;
    public const bool SubroomGeometry = true;
    public static bool TransitionSave(TransitionDurableBaseline baseline, TransitionDraft draft) => baseline.Alias != draft.Alias || baseline.FriendlyName != draft.FriendlyName || baseline.SourceSubroomReferenceText != draft.SourceSubroomReferenceText;
    public static bool CheckSave(CheckDurableBaseline baseline, CheckDraft draft) => baseline.FriendlyName != draft.FriendlyName || baseline.SubroomReferenceText != draft.SubroomReferenceText || baseline.LocationType != draft.LocationType;
    public static bool ConnectionSave(ConnectionDurableBaseline baseline, ConnectionDraft draft) => baseline.Alias != draft.Alias || baseline.FriendlyName != draft.FriendlyName || baseline.SourceSubroomReferenceText != draft.SourceSubroomReferenceText || baseline.DestinationSubroomReferenceText != draft.DestinationSubroomReferenceText;
    public static bool TransitionMetadata(TransitionDurableBaseline baseline, TransitionInGameMetadataDraft draft) => baseline.InGamePositionX != draft.InGamePositionX || baseline.InGamePositionY != draft.InGamePositionY || baseline.AnnotationSceneUnitX != draft.AnnotationSceneUnitX || baseline.AnnotationSceneUnitY != draft.AnnotationSceneUnitY;
    public static bool CheckMetadata(CheckMetadataDurableBaseline baseline, CheckInGameMetadataDraft draft) => baseline.InGamePositionX != draft.InGamePositionX || baseline.InGamePositionY != draft.InGamePositionY || baseline.AnnotationSceneUnitX != draft.AnnotationSceneUnitX || baseline.AnnotationSceneUnitY != draft.AnnotationSceneUnitY;
    public const bool Create = true;
    public const bool ArchiveRestoreDelete = true;
    public const bool Reorder = false;
    public const bool SubroomReferenceProposal = true;
    public const bool TransitionInverseEditProposal = false;
    public const bool TransitionInverseCreateProposal = true;
    public const bool RoomLifecycle = true;
}

// Internal scalar load records do not cross the service/component boundary.
internal sealed record RoomHeaderLoad(Guid Id, string FriendlyName, string ReferenceId, string? InGameId, string? Contributors, string? Comments, double MinX, double MinY, double? MaxX, double? MaxY, double? ScaleX, double? ScaleY, double? PixelOffsetX, double? PixelOffsetY, bool UseSceneImage, bool Stale, bool Archived, bool CanExportZone, DateTime UpdatedUtc);
internal sealed record RoomReferenceLoad(Guid Id, string ReferenceId, string? InGameId, bool Archived);
internal sealed record SubroomLoad(Guid Id, Guid RoomId, string ReferenceId, string FriendlyName, string Notes, double? X, double? Y, double? Width, double? Height, bool AnnotationEnabled, int SortOrder, bool Archived, DateTime UpdatedUtc);
internal sealed record TransitionLoad(Guid Id, Guid RoomId, string Alias, string FriendlyName, string? InGameId, double? InGameX, double? InGameY, double? InGameZ, double? LocalX, double? LocalY, double? LocalZ, double? AnnotationX, double? AnnotationY, string? Source, string? DestinationRoom, string? DestinationAlias, string Requirements, string Notes, Guid? SourceId, Guid? DestinationRoomId, Guid? DestinationId, int SortOrder, bool Todo, bool? Verified, bool Archived, DateTime UpdatedUtc, bool EnableAnnotation, bool? RequirementsParseSucceeded, bool IsUnsafeArrival);
internal sealed record ConnectionLoad(Guid Id, Guid RoomId, string Alias, string FriendlyName, string Source, string Destination, string Requirements, string Notes, bool AnnotationEnabled, double? X, double? Y, Guid? SourceId, Guid? DestinationId, int SortOrder, bool Archived, bool Todo, bool? Verified, DateTime UpdatedUtc, bool? RequirementsParseSucceeded);
internal sealed record CheckLoad(Guid Id, Guid RoomId, string FriendlyName, string? InGameId, double? InGameX, double? InGameY, double? InGameZ, double? LocalX, double? LocalY, double? LocalZ, double? AnnotationX, double? AnnotationY, string? Subroom, string Requirements, string Notes, string? LocationType, bool AnnotationEnabled, Guid? SubroomId, int SortOrder, bool Todo, bool? Verified, bool Archived, DateTime UpdatedUtc, bool? RequirementsParseSucceeded);
internal sealed record TransitionFactLoad(Guid Id, Guid RoomId, string Alias, Guid? DestinationRoomId, Guid? DestinationId, string? InGameId, bool Archived, string? DestinationRoomReferenceText = null, string? DestinationTransitionAliasText = null);
internal sealed record CheckFactLoad(Guid Id, Guid RoomId, string FriendlyName);
internal sealed record TransitionFacts(IReadOnlyList<TransitionFactLoad> Inverses, IReadOnlyList<TransitionFactLoad> DestinationAliases, IReadOnlyList<TransitionFactLoad> InGameConflicts);
internal sealed record CheckFacts(IReadOnlyList<CheckFactLoad> NameConflicts);

internal static class RoomHeaderLoader { internal static Task<RoomHeaderLoad?> LoadAsync(LogicDbContext db, Guid id, CancellationToken token) => db.Rooms.AsNoTracking().Where(x => x.Id == id).Select(x => new RoomHeaderLoad(x.Id,x.FriendlyName,x.ReferenceId,x.InGameId,x.Contributors,x.Comments,x.SceneMinX,x.SceneMinY,x.SceneMaxX,x.SceneMaxY,x.SceneImagePixelsPerUnitX,x.SceneImagePixelsPerUnitY,x.SceneImagePixelOffsetX,x.SceneImagePixelOffsetY,x.UseSceneImage,x.IsSceneImageStale,x.IsArchived,x.RoomGroupId != null,x.UpdatedUtc)).SingleOrDefaultAsync(token); }
internal static class RoomSuggestionLoader { internal static Task<List<RoomReferenceLoad>> LoadAsync(LogicDbContext db, CancellationToken token) => db.Rooms.AsNoTracking().Where(x => !x.IsArchived).Select(x => new RoomReferenceLoad(x.Id,x.ReferenceId,x.InGameId,false)).ToListAsync(token); }
internal static class RoomReferenceCandidateLoader
{
    internal static Task<List<RoomReferenceLoad>> LoadAsync(LogicDbContext db, IReadOnlyList<TransitionLoad> rows, CancellationToken token)
    {
        var keys = rows.Select(x => x.DestinationRoom).Where(MapperSupport.Text).Select(MapperSupport.Key).Distinct().ToArray();
        // The impossible ID keeps the bounded candidate query observable even when
        // the selected room has no authored destination text; it never broadens it.
        return db.Rooms.AsNoTracking().Where(x => keys.Contains(x.ReferenceId.Trim().ToUpper()) || x.Id == Guid.Empty).Select(x => new RoomReferenceLoad(x.Id,x.ReferenceId,x.InGameId,x.IsArchived)).ToListAsync(token);
    }
}
internal static class SubroomLoader { internal static Task<List<SubroomLoad>> LoadAsync(LogicDbContext db, Guid id, CancellationToken token) => db.Subrooms.AsNoTracking().Where(x => x.RoomId == id).Select(x => new SubroomLoad(x.Id,x.RoomId,x.ReferenceId,x.FriendlyName,x.Notes ?? "",x.SceneUnitX,x.SceneUnitY,x.SceneUnitWidth,x.SceneUnitHeight,x.EnableAnnotation,x.SortOrder,x.IsArchived,x.UpdatedUtc)).ToListAsync(token); }
internal static class TransitionLoader { internal static Task<List<TransitionLoad>> LoadAsync(LogicDbContext db, Guid id, CancellationToken token) => db.RoomTransitions.AsNoTracking().Where(x => x.RoomId == id).Select(x => new TransitionLoad(x.Id,x.RoomId,x.Alias,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SourceSubroomReferenceText,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText,x.Requirements,x.Notes,x.ResolvedSourceSubroomId,x.ResolvedDestinationRoomId,x.ResolvedDestinationTransitionId,x.SortOrder,x.IsTodo,x.IsVerified,x.IsArchived,x.UpdatedUtc, x.EnableAnnotation,x.RequirementsParseSucceeded,x.IsUnsafeArrival)).ToListAsync(token); }
internal static class ConnectionLoader { internal static Task<List<ConnectionLoad>> LoadAsync(LogicDbContext db, Guid id, CancellationToken token) => db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == id).Select(x => new ConnectionLoad(x.Id,x.RoomId,x.Alias,x.FriendlyName,x.SourceSubroomReferenceText,x.DestinationSubroomReferenceText,x.Requirements,x.Notes,x.EnableAnnotation,x.SceneUnitX,x.SceneUnitY,x.ResolvedSourceSubroomId,x.ResolvedDestinationSubroomId,x.SortOrder,x.IsArchived,x.IsTodo,x.IsVerified,x.UpdatedUtc,x.RequirementsParseSucceeded)).ToListAsync(token); }
internal static class CheckLoader { internal static Task<List<CheckLoad>> LoadAsync(LogicDbContext db, Guid id, CancellationToken token) => db.CheckLocations.AsNoTracking().Where(x => x.RoomId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new CheckLoad(x.Id,x.RoomId,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SubroomReferenceText,x.Requirements,x.Notes,x.LocationType,x.EnableAnnotation,x.ResolvedSubroomId,x.SortOrder,x.IsTodo,x.IsVerified,x.IsArchived,x.UpdatedUtc,x.RequirementsParseSucceeded)).ToListAsync(token); }

internal static class TransitionValidationLoader
{
    internal static async Task<TransitionFacts> LoadAsync(LogicDbContext db, Guid roomId, IReadOnlyList<TransitionLoad> rows, CancellationToken token)
    {
        var active = rows.Where(x => !x.Archived).ToArray(); var ids = active.Select(x => x.Id).ToArray();
        var destinations = active.Where(x => x.DestinationRoomId is not null).Select(x => x.DestinationRoomId!.Value).Distinct().ToArray();
        var inverses = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && destinations.Contains(x.RoomId) && x.ResolvedDestinationRoomId == roomId && x.ResolvedDestinationTransitionId != null && ids.Contains(x.ResolvedDestinationTransitionId.Value)).Select(x => new TransitionFactLoad(x.Id,x.RoomId,x.Alias,x.ResolvedDestinationRoomId,x.ResolvedDestinationTransitionId,x.InGameId,x.IsArchived,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText)).ToListAsync(token);
        var aliases = await db.RoomTransitions.AsNoTracking().Where(x => destinations.Contains(x.RoomId)).Select(x => new TransitionFactLoad(x.Id,x.RoomId,x.Alias,x.ResolvedDestinationRoomId,x.ResolvedDestinationTransitionId,x.InGameId,x.IsArchived,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText)).ToListAsync(token);
        // Compare raw same-room values in the mapper with OrdinalIgnoreCase;
        // SQLite's upper-case function is not the comparison contract.
        var games = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.RoomId == roomId && x.InGameId != null).Select(x => new TransitionFactLoad(x.Id,x.RoomId,x.Alias,x.ResolvedDestinationRoomId,x.ResolvedDestinationTransitionId,x.InGameId,false)).ToListAsync(token);
        return new(inverses, aliases, games);
    }
}
internal static class CheckValidationLoader
{
    internal static async Task<CheckFacts> LoadAsync(LogicDbContext db, CancellationToken token)
    {
        // This scalar context projection simultaneously supplies the browser's
        // active check multiplicity and the selected-room duplicate-name facts.
        // Check game IDs need no conflict reader; retain the full name catalogue
        // for parser resolution as well as local/global name diagnostics.
        var namesFacts = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && !x.Room.IsArchived).Select(x => new CheckFactLoad(x.Id,x.RoomId,x.FriendlyName)).ToListAsync(token);
        return new(namesFacts);
    }
}

public static class RoomEditorV2Mapper
{
    internal static RoomEditorV2View Map(RoomHeaderLoad header, IReadOnlyList<RoomReferenceLoad> suggestions, IReadOnlyList<RoomReferenceLoad> candidates, IReadOnlyList<SubroomLoad> subrooms, IReadOnlyList<TransitionLoad> transitions, IReadOnlyList<ConnectionLoad> connections, IReadOnlyList<CheckLoad> checks, TransitionFacts transitionFacts, CheckFacts checkFacts)
    {
        var mappedHeader = RoomHeaderMapper.Map(header, suggestions);
        var mappedSubrooms = SubroomTableMapper.Map(subrooms, transitions, connections, checks);
        var mappedTransitions = TransitionTableMapper.Map(header, suggestions, candidates, subrooms, transitions, transitionFacts);
        var mappedConnections = ConnectionTableMapper.Map(subrooms, connections);
        mappedConnections = new ConnectionTableView(
            ApplyConnectionSelfLoopStatus(
                ApplyConnectionRequirementStatus(mappedConnections.ActiveRows, connections, false, header.Archived),
                connections),
            ApplyConnectionRequirementStatus(mappedConnections.ArchivedRows, connections, true, header.Archived),
            mappedConnections.SubroomReferenceSuggestions);
        var (mappedChecks, mappedEnemies) = CheckTableMapper.Map(subrooms, checks, checkFacts, header.Archived);
        return new(mappedHeader,
            mappedSubrooms, mappedTransitions, mappedConnections, mappedChecks, mappedEnemies);

        static IReadOnlyList<ConnectionRowView> ApplyConnectionRequirementStatus(
            IReadOnlyList<ConnectionRowView> mapped, IReadOnlyList<ConnectionLoad> loads, bool archived, bool owningRoomArchived)
        {
            var byId = loads.ToDictionary(value => value.Id);
            return mapped.Select(row =>
            {
                var load = byId[row.EntityId];
                return row with
                {
                    RequirementsSeverity = archived ? V2Severity.Neutral : owningRoomArchived
                        ? MapperSupport.Required(load.Requirements)
                        : MapperSupport.Requirements(load.Requirements, load.RequirementsParseSucceeded)
                };
            }).ToArray();
        }

        static IReadOnlyList<ConnectionRowView> ApplyConnectionSelfLoopStatus(
            IReadOnlyList<ConnectionRowView> mapped, IReadOnlyList<ConnectionLoad> loads)
        {
            var selfLoopIds = loads.Where(value => !value.Archived && value.SourceId is not null &&
                    value.SourceId == value.DestinationId)
                .Select(value => value.Id).ToHashSet();
            return mapped.Select(row => selfLoopIds.Contains(row.EntityId)
                ? row with
                {
                    SourceReferenceSeverity = V2Severity.Danger,
                    DestinationReferenceSeverity = V2Severity.Danger
                }
                : row).ToArray();
        }

    }
    /// <summary>
    /// Reconciles the complete logical batch. A caller may retain a scene only
    /// for an intentionally skipped same-room scene load; a loaded null scene is
    /// authoritative and clears the prior projection.
    /// </summary>
    public static RoomEditorV2View Reconcile(RoomEditorV2View? current, RoomEditorV2View fresh, bool retainCurrentScene = false) => new(RoomHeaderMapper.Reconcile(current?.Header, fresh.Header), SubroomTableMapper.Reconcile(current?.Subrooms, fresh.Subrooms), TransitionTableMapper.Reconcile(current?.Transitions, fresh.Transitions), ConnectionTableMapper.Reconcile(current?.Connections, fresh.Connections), fresh.CheckLocations, fresh.Enemies)
    {
        RequirementsParserContext = fresh.RequirementsParserContext,
        Scene = fresh.Scene ?? (retainCurrentScene && current?.Header.RoomId == fresh.Header.RoomId ? current.Scene : null)
    };
}

public static class RoomHeaderMapper
{
    internal static RoomHeaderView Map(RoomHeaderLoad x, IReadOnlyList<RoomReferenceLoad> rooms) => new(x.Id,x.FriendlyName,x.ReferenceId,x.InGameId,x.Contributors,x.Comments,x.MinX,x.MinY,x.MaxX,x.MaxY,SceneBounds.HasUsableScales(x.ScaleX,x.ScaleY) && (!x.UseSceneImage || SceneBounds.HasUsablePixelOffsets(x.PixelOffsetX,x.PixelOffsetY)),x.Stale,x.Archived,x.CanExportZone,x.UpdatedUtc,MapperSupport.Required(x.FriendlyName),MapperSupport.Duplicate(x.ReferenceId,rooms.Select(y=>y.ReferenceId)),MapperSupport.Text(x.InGameId)?MapperSupport.Duplicate(x.InGameId!,rooms.Select(y=>y.InGameId)):V2Severity.Neutral);
    internal static RoomHeaderView Reconcile(RoomHeaderView? _, RoomHeaderView fresh) => fresh;
}
public static class SubroomTableMapper
{
    internal static SubroomTableView Map(IReadOnlyList<SubroomLoad> rows,
        IReadOnlyList<TransitionLoad> transitions, IReadOnlyList<ConnectionLoad> connections,
        IReadOnlyList<CheckLoad> checks)
    {
        var active = rows.Where(x => !x.Archived).ToArray();
        var usedIds = transitions.Where(x => !x.Archived).Select(x => x.SourceId)
            .Concat(connections.Where(x => !x.Archived).SelectMany(x => new[] { x.SourceId, x.DestinationId }))
            .Concat(checks.Where(x => !x.Archived).Select(x => x.SubroomId))
            .OfType<Guid>().ToHashSet();
        return new(Map(rows, false), Map(rows, true));

        IReadOnlyList<SubroomRowView> Map(IEnumerable<SubroomLoad> source, bool archived) => source
            .Where(x => x.Archived == archived).OrderBy(x => x.SortOrder)
            .Select(x => new SubroomRowView(x.Id, x.SortOrder, x.Archived, x.UpdatedUtc,
                x.FriendlyName, x.ReferenceId, x.Notes, x.X, x.Y, x.Width, x.Height,
                archived ? V2Severity.Neutral : MapperSupport.Duplicate(x.FriendlyName, active.Select(y => y.FriendlyName)),
                archived ? V2Severity.Neutral : MapperSupport.Duplicate(x.ReferenceId, active.Select(y => y.ReferenceId)),
                !archived && !usedIds.Contains(x.Id) ? V2Severity.Warning : V2Severity.Neutral,
                MapperSupport.Rectangle(x.X, x.Y, x.Width, x.Height), x.AnnotationEnabled)).ToArray();
    }
    internal static SubroomTableView Reconcile(SubroomTableView? _, SubroomTableView fresh)=>fresh;
}
public static class TransitionTableMapper
{
    internal static TransitionTableView Map(RoomHeaderLoad header,IReadOnlyList<RoomReferenceLoad> suggestions,IReadOnlyList<RoomReferenceLoad> candidates,IReadOnlyList<SubroomLoad> subrooms,IReadOnlyList<TransitionLoad> rows,TransitionFacts facts)
    {
        var active=rows.Where(x=>!x.Archived).ToArray(); var activeSubs=subrooms.Where(x=>!x.Archived).ToArray();
        var aliases=rows.ToDictionary(x=>x.Id,x=>AliasSuggestions(x));
        return new(MapRows(false),MapRows(true),suggestions.Select(x=>x.ReferenceId).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray(),aliases) { SubroomReferenceSuggestions=activeSubs.Select(x=>x.ReferenceId).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray() };
        IReadOnlyList<string> AliasSuggestions(TransitionLoad row) => row.DestinationRoomId is Guid target ? facts.DestinationAliases.Where(x=>!x.Archived && x.RoomId==target && MapperSupport.Text(x.Alias)).Select(x=>x.Alias).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray() : [];
        IReadOnlyList<TransitionRowView> MapRows(bool archived)=>rows.Where(x=>x.Archived==archived).OrderBy(x=>x.SortOrder).Select(MapRow).ToArray();
        TransitionRowView MapRow(TransitionLoad x)
        {
            var source=MapperSupport.Reference(x.Source,x.SourceId,subrooms.Select(y=>(y.Id,y.ReferenceId,y.Archived)));
            var room=MapperSupport.Reference(x.DestinationRoom,x.DestinationRoomId,candidates.Select(y=>(y.Id,y.ReferenceId,y.Archived)));
            var alias=DestinationAlias(x,room); var isActive=!x.Archived;
            var pair=isActive && MapperSupport.Text(x.DestinationRoom) && MapperSupport.Text(x.DestinationAlias) && active.Count(y=>MapperSupport.SameEndpoint(y.DestinationRoom,y.DestinationRoomId,x.DestinationRoom,x.DestinationRoomId)&&MapperSupport.SameEndpoint(y.DestinationAlias,y.DestinationId,x.DestinationAlias,x.DestinationId))>1?V2Severity.Danger:V2Severity.Neutral;
            var game=isActive&&MapperSupport.Text(x.InGameId)&&facts.InGameConflicts.Any(y=>!y.Archived&&y.Id!=x.Id&&y.RoomId==x.RoomId&&MapperSupport.Same(y.InGameId,x.InGameId))?V2Severity.Danger:V2Severity.Neutral;
            var inverse = Inverse(x); var inverseTarget = inverse == V2InverseState.One ? facts.Inverses.Single(y => y.RoomId == x.DestinationRoomId && y.DestinationRoomId == header.Id && y.DestinationId == x.Id) : null;
            var resolvedTarget = x.DestinationId is Guid targetId ? facts.DestinationAliases.SingleOrDefault(y => !y.Archived && y.Id == targetId && y.RoomId == x.DestinationRoomId) : null;
            var canAuthorInverse = isActive && resolvedTarget is not null &&
                (string.IsNullOrWhiteSpace(resolvedTarget.DestinationRoomReferenceText) || string.IsNullOrWhiteSpace(resolvedTarget.DestinationTransitionAliasText)) &&
                suggestions.Count(y => !y.Archived && MapperSupport.Same(y.ReferenceId, header.ReferenceId)) == 1 &&
                active.Count(y => MapperSupport.Same(y.Alias, x.Alias)) == 1;
            return new(x.Id,x.SortOrder,x.Archived,x.UpdatedUtc,x.RequirementsParseSucceeded,x.Alias,x.FriendlyName,x.Source,x.DestinationRoom,x.DestinationAlias,x.Requirements,x.Notes,x.Todo,x.Verified,x.InGameId,x.InGameX,x.InGameY,x.InGameZ,x.LocalX,x.LocalY,x.LocalZ,x.AnnotationX,x.AnnotationY,isActive?MapperSupport.Alias(x.Alias,active.Select(y=>y.Alias)):V2Severity.Neutral,isActive?MapperSupport.Duplicate(x.FriendlyName,active.Select(y=>y.FriendlyName)):V2Severity.Neutral,isActive?MapperSupport.ReferenceSeverity(MapperSupport.Presence(x.Source,activeSubs.Any()),source):V2Severity.Neutral,isActive?MapperSupport.ReferenceSeverity(MapperSupport.Required(x.DestinationRoom),room):V2Severity.Neutral,isActive?MapperSupport.ReferenceSeverity(MapperSupport.Max(MapperSupport.Alias(x.DestinationAlias,[]),pair),alias):V2Severity.Neutral,isActive?(header.Archived?MapperSupport.Required(x.Requirements):MapperSupport.Requirements(x.Requirements,x.RequirementsParseSucceeded)):V2Severity.Neutral,game,source,room,alias,inverse,x.IsUnsafeArrival,x.EnableAnnotation,canAuthorInverse,inverseTarget?.RoomId);
        }
        V2ReferenceState DestinationAlias(TransitionLoad x,V2ReferenceState roomState){ if(!MapperSupport.Text(x.DestinationAlias))return V2ReferenceState.None; if(roomState==V2ReferenceState.TargetArchived)return V2ReferenceState.TargetArchived; if(roomState!=V2ReferenceState.Resolved||x.DestinationRoomId is null)return x.DestinationId is null?V2ReferenceState.Unresolved:V2ReferenceState.OutOfSync; var matches=facts.DestinationAliases.Where(y=>!y.Archived&&y.RoomId==x.DestinationRoomId&&MapperSupport.Same(y.Alias,x.DestinationAlias)).ToArray(); if(matches.Length==0){var archived=facts.DestinationAliases.Any(y=>y.Archived&&y.RoomId==x.DestinationRoomId&&MapperSupport.Same(y.Alias,x.DestinationAlias));return archived?V2ReferenceState.TargetArchived:x.DestinationId is null?V2ReferenceState.Unresolved:V2ReferenceState.OutOfSync;} return matches.Length>1?V2ReferenceState.Ambiguous:matches[0].Id==x.DestinationId?V2ReferenceState.Resolved:V2ReferenceState.OutOfSync; }
        V2InverseState Inverse(TransitionLoad x)=>x.DestinationRoomId is null||x.DestinationId is null?V2InverseState.None:facts.Inverses.Count(y=>y.RoomId==x.DestinationRoomId&&y.DestinationRoomId==header.Id&&y.DestinationId==x.Id) switch{0=>V2InverseState.Zero,1=>V2InverseState.One,_=>V2InverseState.Multiple};
    }
    internal static TransitionTableView Reconcile(TransitionTableView? _,TransitionTableView fresh)=>fresh;
}
public static class ConnectionTableMapper
{
    internal static ConnectionTableView Map(IReadOnlyList<SubroomLoad> subrooms,IReadOnlyList<ConnectionLoad> rows){var activeSubrooms=subrooms.Where(x=>!x.Archived).ToArray();var suggestions=activeSubrooms.Select(x=>x.ReferenceId).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();return new(MapRows(false),MapRows(true),suggestions); IReadOnlyList<ConnectionRowView> MapRows(bool archived)=>rows.Where(x=>x.Archived==archived).OrderBy(x=>x.SortOrder).Select(MapRow).ToArray(); ConnectionRowView MapRow(ConnectionLoad x){var s=MapperSupport.Reference(x.Source,x.SourceId,subrooms.Select(y=>(y.Id,y.ReferenceId,y.Archived)));var d=MapperSupport.Reference(x.Destination,x.DestinationId,subrooms.Select(y=>(y.Id,y.ReferenceId,y.Archived)));var path=Pathway(x);var a=!x.Archived;return new(x.Id,x.SortOrder,x.Archived,x.UpdatedUtc,x.RequirementsParseSucceeded,x.Alias,x.FriendlyName,x.Source,x.Destination,x.Requirements,x.Notes,x.Todo,x.Verified,x.AnnotationEnabled,x.X,x.Y,a?MapperSupport.Alias(x.Alias,[]):V2Severity.Neutral,a?MapperSupport.Required(x.FriendlyName):V2Severity.Neutral,a?MapperSupport.ReferenceSeverity(MapperSupport.Presence(x.Source,activeSubrooms.Any()),s):V2Severity.Neutral,a?MapperSupport.ReferenceSeverity(MapperSupport.Presence(x.Destination,activeSubrooms.Any()),d):V2Severity.Neutral,a?MapperSupport.Required(x.Requirements):V2Severity.Neutral,path.Severity,!x.Archived&&!activeSubrooms.Any()?V2Severity.Danger:V2Severity.Neutral,s,d,path.State,!x.Archived&&path.Severity==V2Severity.Neutral&&path.State=="one-way"&&x.SourceId is not null&&x.DestinationId is not null&&x.SourceId!=x.DestinationId);} (V2Severity Severity,string State) Pathway(ConnectionLoad x){if(x.Archived)return(V2Severity.Neutral,"unresolved");var group=rows.Where(y=>!y.Archived&&MapperSupport.Same(y.Alias,x.Alias)).ToArray();var names=rows.Where(y=>!y.Archived&&MapperSupport.Same(y.FriendlyName,x.FriendlyName)).ToArray();var bad=group.Length>2||group.Any(y=>!MapperSupport.Same(y.FriendlyName,x.FriendlyName)||!MapperSupport.SamePair(y,x))||group.Any(y=>y.Id!=x.Id&&MapperSupport.SameDirection(y,x))||names.Any(y=>!MapperSupport.Same(y.Alias,x.Alias));if(x.SourceId is null||x.DestinationId is null)return(bad?V2Severity.Danger:V2Severity.Neutral,"unresolved");var reverse=group.Count(y=>y.Id!=x.Id&&y.SourceId==x.DestinationId&&y.DestinationId==x.SourceId);return(bad?V2Severity.Danger:V2Severity.Neutral,reverse switch{0=>"one-way",1=>"bidirectional",_=>"ambiguous reverse match"});}}
    internal static ConnectionTableView Reconcile(ConnectionTableView? _,ConnectionTableView fresh)=>fresh;
    public static IReadOnlyList<ConnectionRowView> MapPlacementEligibility(IReadOnlyList<ConnectionRowView> rows)
    {
        var groups = rows.Where(row => !row.IsArchived).GroupBy(row => row.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.SortOrder).ThenBy(row => row.EntityId).First(), StringComparer.OrdinalIgnoreCase);
        return rows.Select(row =>
        {
            var canonical = row.IsArchived ? row : groups[row.Alias.Trim()];
            var eligible = !row.IsArchived && row.AliasSeverity == V2Severity.Neutral &&
                row.FriendlyNameSeverity == V2Severity.Neutral && row.PathwaySeverity == V2Severity.Neutral &&
                row.NoSubroomSeverity == V2Severity.Neutral;
            var enabled = canonical.EnableAnnotation;
            var positioned = canonical.SceneUnitX is double x && canonical.SceneUnitY is double y && double.IsFinite(x) && double.IsFinite(y);
            return row with
            {
                CanPlaceAnnotation = eligible && (!enabled || !positioned),
                CanRemoveAnnotation = eligible && enabled && positioned,
                HasCompleteAnnotationCoordinates = positioned,
                DurableSceneUnitX = row.SceneUnitX,
                DurableSceneUnitY = row.SceneUnitY,
                EffectiveEnableAnnotation = enabled,
                AnnotationMarkerEntityId = eligible ? canonical.EntityId : null
            };
        }).ToArray();
    }
}
public static class CheckTableMapper
{
    internal static (CheckLocationsTableView CheckLocations, EnemiesTableView Enemies) Map(
        IReadOnlyList<SubroomLoad> subrooms, IReadOnlyList<CheckLoad> rows,
        CheckFacts facts, bool owningRoomArchived)
    {
        var activeSubrooms = subrooms.Where(x => !x.Archived).ToArray();
        var suggestions = Array.AsReadOnly(activeSubrooms.Select(x => x.ReferenceId)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        var types = CheckLocationTypeCatalogue.Definitions;
        // Index the complete active catalogue before section partitioning. Keep
        // authored facts intact for parser lookup and cross-room-only warnings.
        var localNames = facts.NameConflicts.Where(x => MapperSupport.Text(x.FriendlyName))
            .GroupBy(x => (x.RoomId, Key: RoomGraphExportService.CanonicalValue(x.FriendlyName) ?? MapperSupport.Key(x.FriendlyName)))
            .ToDictionary(x => x.Key, x => x.Count());
        var authoredNameRooms = facts.NameConflicts.Where(x => MapperSupport.Text(x.FriendlyName))
            .GroupBy(x => x.FriendlyName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.RoomId).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        var checksActive = new List<CheckRowView>(); var checksArchived = new List<CheckRowView>();
        var enemiesActive = new List<CheckRowView>(); var enemiesArchived = new List<CheckRowView>();
        foreach (var x in rows.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            var state = MapperSupport.Reference(x.Subroom, x.SubroomId, subrooms.Select(y => (y.Id, y.ReferenceId, y.Archived)));
            var active = !x.Archived;
            var name = !MapperSupport.Text(x.FriendlyName) ? V2Severity.Warning
                : localNames.GetValueOrDefault((x.RoomId, RoomGraphExportService.CanonicalValue(x.FriendlyName) ?? MapperSupport.Key(x.FriendlyName))) > 1 ? V2Severity.Danger
                : authoredNameRooms.TryGetValue(x.FriendlyName.Trim(), out var nameRooms) &&
                    (nameRooms.Length > 1 || nameRooms.Length == 1 && nameRooms[0] != x.RoomId) ? V2Severity.Warning : V2Severity.Neutral;
            var definition = types.FirstOrDefault(y => string.Equals(y.OutputValue, x.LocationType, StringComparison.Ordinal));
            var isEnemy = definition?.Group == "enemies";
            var row = new CheckRowView(x.Id, x.SortOrder, x.Archived, x.UpdatedUtc, x.RequirementsParseSucceeded,
                x.FriendlyName, x.Subroom, x.Requirements, x.Notes, x.LocationType,
                x.LocationType is null ? "Unknown" : definition?.Name ?? $"Unrecognized: {x.LocationType}",
                x.Todo, x.Verified, x.InGameId, x.InGameX, x.InGameY, x.InGameZ, x.LocalX, x.LocalY, x.LocalZ,
                x.AnnotationX, x.AnnotationY, x.AnnotationEnabled, active ? name : V2Severity.Neutral,
                active ? MapperSupport.ReferenceSeverity(MapperSupport.Presence(x.Subroom, activeSubrooms.Length > 0), state) : V2Severity.Neutral,
                !active ? V2Severity.Neutral : owningRoomArchived ? MapperSupport.Required(x.Requirements)
                    : MapperSupport.Requirements(x.Requirements, x.RequirementsParseSucceeded),
                V2Severity.Neutral, MapperSupport.Pair(x.AnnotationX, x.AnnotationY),
                active && !owningRoomArchived && definition is null ? V2Severity.Danger : V2Severity.Neutral, state)
                { IsEnemy = isEnemy };
            (isEnemy ? active ? enemiesActive : enemiesArchived : active ? checksActive : checksArchived).Add(row);
        }
        return (new(checksActive.ToArray(), checksArchived.ToArray(), suggestions, types),
            new(enemiesActive.ToArray(), enemiesArchived.ToArray(), suggestions, types));
    }
}
internal static class MapperSupport
{
    internal static bool Text(string? x)=>!string.IsNullOrWhiteSpace(x); internal static string Key(string? x)=>x!.Trim().ToUpperInvariant(); internal static bool Same(string? a,string? b)=>Text(a)&&Text(b)&&string.Equals(a!.Trim(),b!.Trim(),StringComparison.OrdinalIgnoreCase); internal static V2Severity Required(string? x)=>Text(x)?V2Severity.Neutral:V2Severity.Warning; internal static V2Severity Requirements(string? x,bool? succeeded)=>succeeded switch{false=>V2Severity.Danger,null=>V2Severity.Warning,_=>Required(x)}; internal static V2Severity Duplicate(string? x,IEnumerable<string?> xs)=>Text(x)&&xs.Count(y=>Same(x,y))>1?V2Severity.Danger:Required(x); internal static V2Severity Alias(string? x,IEnumerable<string?> xs)=>!Text(x)?V2Severity.Warning:x!.Length>3||xs.Count(y=>Same(x,y))>1?V2Severity.Danger:V2Severity.Neutral; internal static V2Severity Presence(string? x,bool has)=>has==Text(x)?V2Severity.Neutral:V2Severity.Warning; internal static V2Severity Max(V2Severity a,V2Severity b)=>(V2Severity)Math.Max((int)a,(int)b); internal static V2Severity Pair(double? x,double? y)=>x is null&&y is null||x is double a&&y is double b&&double.IsFinite(a)&&double.IsFinite(b)?V2Severity.Neutral:V2Severity.Danger; internal static V2Severity Rectangle(double? x,double? y,double? w,double? h)=>x is null&&y is null&&w is null&&h is null||x is double a&&y is double b&&w is double c&&h is double d&&double.IsFinite(a)&&double.IsFinite(b)&&double.IsFinite(c)&&double.IsFinite(d)&&c>0&&d>0?V2Severity.Neutral:V2Severity.Danger;
    internal static V2Severity ReferenceSeverity(V2Severity normal,V2ReferenceState state)=>state is V2ReferenceState.Unresolved or V2ReferenceState.Ambiguous or V2ReferenceState.OutOfSync or V2ReferenceState.TargetArchived?V2Severity.Danger:normal;
    internal static V2ReferenceState Reference(string? text,Guid? id,IEnumerable<(Guid Id,string Text,bool Archived)> candidates){if(!Text(text))return V2ReferenceState.None;var matches=candidates.Where(x=>Same(x.Text,text)).ToArray();var active=matches.Where(x=>!x.Archived).ToArray();if(active.Length==1)return active[0].Id==id?V2ReferenceState.Resolved:V2ReferenceState.OutOfSync;if(active.Length>1)return V2ReferenceState.Ambiguous;if(matches.Any(x=>x.Archived))return V2ReferenceState.TargetArchived;return id is null?V2ReferenceState.Unresolved:V2ReferenceState.OutOfSync;}
    internal static bool SameEndpoint(string? a,Guid? aid,string? b,Guid? bid)=>aid is not null&&bid is not null?aid==bid:Same(a,b); internal static bool SameDirection(ConnectionLoad a,ConnectionLoad b)=>SameEndpoint(a.Source,a.SourceId,b.Source,b.SourceId)&&SameEndpoint(a.Destination,a.DestinationId,b.Destination,b.DestinationId); internal static bool SamePair(ConnectionLoad a,ConnectionLoad b)=>SameDirection(a,b)||SameEndpoint(a.Source,a.SourceId,b.Destination,b.DestinationId)&&SameEndpoint(a.Destination,a.DestinationId,b.Source,b.SourceId);
}
