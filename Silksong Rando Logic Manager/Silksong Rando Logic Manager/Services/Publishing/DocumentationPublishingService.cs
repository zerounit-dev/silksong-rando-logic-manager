using LibGit2Sharp;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

public sealed record PublishingChange(string Path, string Kind, string Change, long BeforeBytes, long AfterBytes);
public readonly record struct PublishingPage(Guid Id);
public sealed record PublishingFailure(Guid Id, string Details);
public sealed record PublishingProgress(string Operation, int Step, int Steps, string Task,
    int? RoomsCompleted = null, int? RoomsTotal = null, string? CurrentRoom = null, string? Detail = null);
internal sealed record PublishingLatestPreparation(DateTime RecordedUtc, string User, bool IsOtherAccount);
public sealed record PublishingView(bool Enabled, string Repository, string Branch, bool CredentialsReady,
    bool Busy, string Phase, string Stage, PublishingProgress? Progress, string? CandidateId,
    string? Commit, string? PublishedUrl, string? Error, IReadOnlyList<string> Validation, IReadOnlyList<PublishingChange> Changes,
    PublishingFailure? Failure, IReadOnlyList<PublishingTiming> Timings)
{
    internal PublishingRoomReview? Review { get; init; }
    internal string? ReviewError { get; init; }
    internal long ReviewEpoch { get; init; }
    internal MapPreflightView? MapPreflight { get; init; }
    internal Guid? MapPreparation { get; init; }
    internal Guid? MapDecision { get; init; }
    internal PublishingLatestPreparation? LatestPreparation { get; init; }
}
public sealed record PrepareDocumentation(PublishingPage Page, string? ExpectedCandidateId, long ExpectedEpoch);
public sealed record CommitDocumentation(PublishingPage Page, string CandidateId, string Message);
public sealed record RetryDocumentationPush(PublishingPage Page, string Commit);
public sealed record DiscardDocumentation(PublishingPage Page, string? CandidateId);

/// <summary>
/// Process-owned operation lifetime, independent of circuits. Security integration
/// must authorize every public command/read at execution (including after gates),
/// not just the /tools/publishing route. No ambient missing principal means Admin.
/// </summary>
public sealed partial class DocumentationPublishingService : IDisposable, IAsyncDisposable
{
    private readonly PublishingStorage storage;
    private readonly PublishingSettings settings;
    private readonly PublishingGit git;
    private readonly SceneImageFileService images;
    private readonly SharedPublicationCoordinator publications;
    private readonly string liveDatabase;
    private readonly string font;
    private readonly ILogger<DocumentationPublishingService> logger;
    private readonly object gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Subscription> subscribers = new();
    private readonly SemaphoreSlim commandAdmission = new(1, 1);
    private readonly IServiceScopeFactory scopes;
    private PublishingJournal? journal;
    private bool busy, stopping;
    private Task? initialization;
    private Task? activeJob;
    private string stage = "Not prepared", phase = "Idle";
    private PublishingProgress? progress;
    // Advisory only: one admitted start for this service lifetime, not restored from the journal.
    private sealed record PreparationStart(DateTime RecordedUtc, string Account, string User);
    private PreparationStart? latestPreparation;
    private readonly CancellationToken applicationStopping;
    private static readonly string[] PreparationTasks =
    [
        "Check the publishing repository and download remote changes if needed.",
        "Copy a consistent database snapshot and the available room images.",
        "Load room records and check that they are ready for publication.",
        "Export the requirement catalogue used by the published logic.",
        "Export the complete room catalogue.",
        "Export the connections and logic linking rooms.",
        "Validate map data.",
        "Export room-map coordinates and boundaries.",
        "Write room pages, annotated images and documentation indexes.",
        "Verify generated files and compare them with the current publication.",
        "Save the candidate for review and remove temporary image copies."
    ];
    private static PublishingProgress PreparationStep(int step) => new("Preparation", step, PreparationTasks.Length, PreparationTasks[step - 1]);
    private static PublishingProgress CommitStep(int step) => new("Commit and push", step, 2, step == 1
        ? "Validate the reviewed candidate and create its publication commit."
        : "Check the remote, push the exact publication commit if needed and confirm publication and cleanup.");
    private static PublishingProgress RetryStep() => new("Retry push", 1, 1,
        "Check the remote, push the retained commit if needed and confirm publication and cleanup.");
    private string? error, publishedUrl;
    private PublishingFailure? failure;
    private string? failureOwner;
    private PublishingPage failurePage;
    private PublishingTimings? timings;
    private string? timingOwner;
    private PublishingPage timingPage;
    private PublishingTimingScope? taskTiming;
    public PublishingTimings CreatePreviewTimings() => new(logger);
    // Gate-owned immutable projection. The operation owns the mutable journal;
    // reads must never traverse it while Git/generation/recovery can mutate it.
    private string? candidateId, pendingCommit;
    private IReadOnlyList<string> validation = [];
    private IReadOnlyList<PublishingChange> changes = [];

    public IDisposable Subscribe(Func<Task> changed)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(changed, () => subscribers.TryRemove(id, out _),
            exception => logger.LogError(exception, "Refreshing documentation publishing subscriber failed."));
        subscribers[id] = subscription;
        return subscription;
    }
    private sealed class Subscription(Func<Task> changed, Action unsubscribe, Action<Exception> report) : IDisposable
    {
        private Action? remove = unsubscribe;
        private int pending, running, retired;
        public void Dispose()
        {
            Interlocked.Exchange(ref retired, 1);
            Interlocked.Exchange(ref remove, null)?.Invoke();
        }
        public void Signal()
        {
            if (Volatile.Read(ref retired) != 0) return;
            Interlocked.Exchange(ref pending, 1);
            if (Interlocked.CompareExchange(ref running, 1, 0) == 0) _ = Task.Run(DrainAsync);
        }
        private async Task DrainAsync()
        {
            try
            {
                while (Interlocked.Exchange(ref pending, 0) != 0 && Volatile.Read(ref retired) == 0)
                {
                    try { await changed(); }
                    catch (Exception exception) { report(exception); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
                // Close the signal/worker-exit race without a timer or idle worker.
                if (Volatile.Read(ref pending) != 0 && Volatile.Read(ref retired) == 0) Signal();
            }
        }
    }
    // Only call after releasing gate. No subscriber or renderer work is awaited by
    // the publisher, and each callback has its own observed failure boundary.
    private void NotifyChanged()
    {
        foreach (var subscriber in subscribers.Values)
            subscriber.Signal();
    }

    internal static void Register(WebApplicationBuilder builder)
    {
        // Capture only publishing values from the finalized native graph. The
        // service/workspace stays lazy; configuration reload never changes a job's
        // credentials or repository identity partway through process ownership.
        var settings = PublishingSettings.Read(builder.Configuration);
        builder.Services.AddSingleton(provider => new DocumentationPublishingService(settings,
            provider.GetRequiredService<IWebHostEnvironment>(), provider.GetRequiredService<ApplicationStoragePaths>(),
            provider.GetRequiredService<SceneImageFileService>(), provider.GetRequiredService<ILogger<DocumentationPublishingService>>(),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<SharedPublicationCoordinator>(),
            provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
    }

    internal DocumentationPublishingService(PublishingSettings settings, IWebHostEnvironment environment, ApplicationStoragePaths paths, SceneImageFileService images,
        ILogger<DocumentationPublishingService> logger, IServiceScopeFactory scopes, SharedPublicationCoordinator publications,
        CancellationToken applicationStopping)
    {
        this.settings = settings;
        storage = new(paths.DataRoot); git = new(storage, settings); this.images = images;
        liveDatabase = paths.LogicDatabase;
        font = Path.Combine(environment.ContentRootPath, "Assets", "Publishing", "NotoSans-Bold.ttf");
        this.logger = logger;
        this.scopes = scopes;
        this.publications = publications;
        this.applicationStopping = applicationStopping;
    }
    public async Task<PublishingView> ReadAsync(LiveAccess access, PublishingPage page, Guid? timingOperation = null, long timingSequence = 0)
    {
        var initiatingActor = await access.RequireAsync(AccessPolicies.PublishDocumentation);
        var restore = EnsureInitializedAsync(access, initiatingActor.Owner, page);
        await access.RequireOwnerAsync(initiatingActor.Owner, AccessPolicies.PublishDocumentation);
        lock (gate)
        {
            var actor = access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            ObjectDisposedException.ThrowIf(stopping, this);
            // Invalid configuration must not echo a URL with embedded credentials.
            var safeUrl = "Not configured";
            try { settings.Validate(); safeUrl = settings.RepositoryUrl; } catch (PublishingBlock) { }
            if (!restore.IsCompletedSuccessfully)
                return new(settings.Enabled, safeUrl, settings.Branch, settings.CredentialsReady, true, "Restoring",
                    "Checking the retained publishing workspace and building any prepared review.", null,
                    null, null, null, null, [], [], null, []);
            return new(settings.Enabled, safeUrl, settings.Branch, settings.CredentialsReady, busy, phase, stage, progress,
                CandidateId(), pendingCommit, publishedUrl, error, validation, changes,
                failureOwner == actor.Owner && failurePage == page ? failure : null,
                timingOwner == actor.Owner && timingPage == page ? timings?.Read(timingOperation, timingSequence) ?? [] : [])
            { Review = !busy && phase == "Prepared" ? roomReview : null, ReviewError = phase == "Prepared" ? reviewError : null, ReviewEpoch = reviewAdmissionVersion,
                MapPreflight = mapPreflight, MapPreparation = mapPreparation,
                MapDecision = pendingMapDecision?.Id,
                LatestPreparation = latestPreparation is { } latest
                    ? new(latest.RecordedUtc, latest.User, !string.Equals(latest.Account, actor.Account, StringComparison.Ordinal))
                    : null };
        }
    }
    private Task EnsureInitializedAsync(LiveAccess access, string owner, PublishingPage page)
    {
        lock (gate)
        {
            access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            ObjectDisposedException.ThrowIf(stopping, this);
            // One exclusive restore before any reader/command gets at the workspace.
            if (initialization is null)
            {
                initialization = Task.Run(() => Initialize(owner, page));
                // Signal only after the task has completed, not while a refresh
                // can still observe the restoring projection and miss the edge.
                _ = initialization.ContinueWith(_ => NotifyChanged(), TaskScheduler.Default);
            }
            return initialization;
        }
    }
    private void Initialize(string owner, PublishingPage page)
    {
        try
        {
            // Keep both restoration and identity projection inside this boundary.
            // Load validates retained structure before journal is assigned or used.
            try { InitializeJournal(); }
            catch (PublishingBlock ex) { error = ex.Message; phase = journal?.Phase is "Prepared" or "Interrupted" or "Failed" ? journal.Phase : "Blocked"; stage = "Publishing is blocked; see the details below"; }
            PublishIdentity();
        }
        catch (Exception ex)
        {
            RetainFailure(ex, owner, page);
            if (journal?.Phase != "Published")
                error = "Publishing initialization failed. See the error details. Preserve the workspace for recovery.";
            phase = "Blocked"; stage = "Publishing initialization failed; preserve retained recovery evidence"; candidateId = pendingCommit = null;
            roomReview = null;
        }
    }
    private void InitializeJournal()
    {
        settings.Validate(); storage.Admit(); journal = storage.Load();
        if (journal is null) return;
        if (journal.Configuration != settings.Identity) throw new PublishingBlock($"Current repository/branch configuration differs from the operation retained in Staging/current.json (phase {journal.Phase}). Compare DocumentationPublishing:RepositoryUrl and :Branch with the owned checkout's destination before resuming; keep Repository, Candidate and Rollback intact. Do not discard or push evidence into a different destination. Configuration identity hashes are not displayed.");
        if (journal.Phase == "Published") { storage.Clear(); journal = null; stage = "Previous publication was confirmed; prepare a new candidate"; return; }
        if (journal.Phase is "Replacing" or "Advancing") git.Recover(journal);
        if (journal.Phase == "Preparing") { journal.Phase = "Interrupted"; storage.Save(journal); }
        if (journal.Phase == "Prepared")
        {
            using var restored = CreatePreviewTimings().Begin("Restore prepared review readiness");
            var lengths = restored.Measure(PublishingMetric.Validation, () => storage.ValidateForReview(journal));
            if (journal.Renderer != PublishingRenderer.Version) throw new PublishingBlock($"Retained prepared changes use a different renderer.\nPrepared version: {journal.Renderer}\nCurrent application: {PublishingRenderer.Version}\nDiscard the prepared changes, prepare again and explicitly review the renderer change.");
            using var repo = git.Open(); git.EnsureClean(repo);
            if (repo.Head.Tip.Sha != journal.BaseCommit) throw new PublishingBlock($"Retained prepared base differs from HEAD.\nPrepared base commit: {journal.BaseCommit}\nObserved HEAD commit: {repo.Head.Tip.Sha}\nCompare these commits to confirm the intended checkout history, then discard prepared changes and prepare/review against that history.");
            using (var delta = restored.Segment(PublishingMetric.Review))
            {
                changes = Review(repo, journal, lengths);
                delta.Complete();
            }
            (roomReview, reviewError) = BuildReview(repo, journal, changes, restored);
            RestorePreparationReport(journal);
            if (roomReview is not null) restored.Complete();
        }
        phase = journal.Phase;
        stage = phase switch
        {
            "Prepared" => changes.Count == 0 ? "No changes; nothing to publish" : "Ready for review; not yet published",
            "PendingPush" => "Exact publication commit retained; retry to reconcile and confirm publication",
            "Interrupted" => "Preparation interrupted; discard and prepare again",
            "Failed" => "Previous operation failed; retained evidence is available for recovery",
            _ => "Recovered operation requires reconciliation"
        };
    }
    private string? CandidateId() => phase == "Blocked" ? null : candidateId;
    private void RestorePreparationReport(PublishingJournal retainedJournal)
    {
        // Optional display metadata is not a new candidate-admission gate.
        // Old, malformed or mismatched reports are truthfully unavailable.
        if (retainedJournal.PreparationReport is not { } json) return;
        try
        {
            var retained = json.Deserialize<PublishingPreparationReport>();
            if (retained is not { Report: { Finished: true, AwaitingDecision: false, Checks: not null } report }
                || retained.CandidateId != PublishingStorage.CandidateIdentity(retainedJournal)
                || retained.Preparation == Guid.Empty
                || !UsableDisplayReport(report)) return;
            mapPreflight = report;
            mapPreparation = retained.Preparation;
        }
        catch (JsonException) { logger.LogWarning("The retained candidate's preparation display report is unavailable."); }
    }
    private static bool UsableDisplayReport(MapPreflightView report)
    {
        if (report.Checks.Count == 0) return false;
        foreach (var row in report.Checks)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Group) || string.IsNullOrWhiteSpace(row.Name)
                || string.IsNullOrWhiteSpace(row.Description) || row.Status is not (MapCheckStatus.Pass or MapCheckStatus.Fail)) return false;
            if (row.Status == MapCheckStatus.Pass && (row.Report is not null || row.NotRun is not null)) return false;
            if (row.Status == MapCheckStatus.Fail && row.Report is null && string.IsNullOrWhiteSpace(row.NotRun)) return false;
            if (row.Report is not { } findings) continue;
            if (findings.Zones is null || findings.Count <= 0) return false;
            var total = 0;
            foreach (var zone in findings.Zones)
            {
                if (zone is null || zone.Label is null || zone.Rooms is null) return false;
                var zoneTotal = 0;
                foreach (var room in zone.Rooms)
                {
                    if (room is null || room.Label is null || room.Records is null) return false;
                    var count = 0;
                    foreach (var record in room.Records)
                    {
                        if (record is null || record.Title is null || record.Reasons is null) return false;
                        foreach (var reason in record.Reasons)
                        {
                            if (reason is null || reason.Detail is null || reason.Rooms is null
                                || reason.Rooms.Any(x => x is null || x.Label is null)) return false;
                            count++;
                        }
                    }
                    if (count != room.Count) return false;
                    zoneTotal += count;
                }
                if (zoneTotal != zone.Count) return false;
                total += zoneTotal;
            }
            if (total != findings.Count) return false;
        }
        return true;
    }
    // Called during exclusive initialization or under gate at a quiescent operation boundary,
    // never from ordinary status/progress reads. No manifest/list reference escapes.
    private void PublishIdentity()
    {
        candidateId = journal is null || phase == "Blocked" ? null : PublishingStorage.CandidateIdentity(journal);
        pendingCommit = journal?.Phase == "PendingPush" ? journal.Commit : null;
    }
    public Task<bool> PrepareAsync(LiveAccess access, PrepareDocumentation command) => StartAuthorizedAsync(access, command.Page, PreparationStep(1), async execution =>
    {
        // Busy remains owned throughout cleanup and fresh acquisition. No separately
        // admitted discard/prepare gap permits another publisher to intervene.
        if (journal is not null) storage.Clear();
        await PrepareAsync(execution);
    }, () =>
    {
        if (command.ExpectedCandidateId != CandidateId() || command.ExpectedEpoch != reviewAdmissionVersion)
            throw new PublishingBlock("The preparation request refers to a different result. Read the current result before preparing again.");
        if (journal is not null) ValidateDiscard(command.ExpectedCandidateId);
    });
    public Task<bool> CommitAndPushAsync(LiveAccess access, CommitDocumentation command) => StartAuthorizedAsync(access, command.Page, CommitStep(1), async _ =>
    {
        BeginTask(CommitStep(1));
        git.Commit(journal!, command.Message);
        lock (gate) { phase = journal!.Phase; PublishIdentity(); }
        NotifyChanged();
        BeginTask(CommitStep(2));
        await PushAsync();
    }, () =>
    {
        if (journal?.Phase != "Prepared") throw new PublishingBlock($"Commit requires Prepared phase; current retained phase is {journal?.Phase ?? "none"}. Wait for active work or prepare and review changes before committing; unresolved commit/rollback evidence must be resolved first.");
        if (command.CandidateId != CandidateId()) throw new PublishingBlock("The commit request refers to a different preparation than the current review. Reopen the current prepared changes, review them and enter the commit request again; an older review cannot authorize this candidate.");
        if (changes.Count == 0) throw new PublishingBlock("No changes; no empty commit will be created.");
    });
    public Task<bool> RetryPushAsync(LiveAccess access, RetryDocumentationPush command) => StartAuthorizedAsync(access, command.Page, RetryStep(), _ => { BeginTask(RetryStep()); return PushAsync(); }, () =>
    {
        if (journal?.Phase != "PendingPush") throw new PublishingBlock($"Retry push requires PendingPush phase; current retained phase is {journal?.Phase ?? "none"}. Read the current operation result before choosing an action; retry does not create or recover a missing commit.");
        if (journal.Commit != command.Commit) throw new PublishingBlock($"The retry request does not name the retained pending commit {journal.Commit ?? "(missing)"}. Reopen the current pending result and retry only that exact commit; do not prepare a replacement commit for an unconfirmed push.");
    });
    public Task<bool> DiscardAsync(LiveAccess access, DiscardDocumentation command) => StartAuthorizedAsync(access, command.Page, null, _ =>
    {
        using var discard = timings!.Begin("Discard prepared changes");
        storage.Timing = discard;
        try
        {
        discard.Measure(PublishingMetric.Cleanup, storage.Clear); lock (gate) { journal = null; changes = []; validation = []; mapPreflight = null; mapPreparation = null; phase = "Idle"; stage = "Discarded"; progress = null; PublishIdentity(); }
        NotifyChanged();
        discard.Complete();
        return Task.CompletedTask;
        }
        finally { storage.Timing = null; }
    }, () =>
    {
        ValidateDiscard(command.CandidateId);
    });
    private void ValidateDiscard(string? expectedCandidate)
    {
        if (journal?.Phase is not ("Prepared" or "Interrupted" or "Failed"))
            throw new PublishingBlock($"Discard is not allowed in retained phase {journal?.Phase ?? "none"}{(journal?.Commit is null ? "" : $", commit {journal.Commit}")}. Keep Staging/current.json, Candidate and Rollback; establish whether the retained commit/ref advanced and compare the checkout with the recorded before/after files before cleanup.");
        if (expectedCandidate != CandidateId()) throw new PublishingBlock("The discard request refers to a different preparation than the current view. Reopen the current result and review which prepared changes would be removed before requesting discard again.");
        if (phase == "Blocked") throw new PublishingBlock($"Discard is blocked by the current recovery failure in retained phase {journal.Phase}. Keep Staging/current.json, Candidate and Rollback; resolve the specific file/commit comparison in the preceding failure before cleanup.");
    }
    private async Task<bool> StartAuthorizedAsync(LiveAccess access, PublishingPage page, PublishingProgress? initialProgress, Func<LiveAccess, Task> action, Action validate)
    {
        var actor = await access.RequireAsync(AccessPolicies.PublishDocumentation);
        var presented = access.CapturePrincipal();
        await commandAdmission.WaitAsync();
        try
        {
            await access.RequireOwnerAsync(actor.Owner, AccessPolicies.PublishDocumentation);
            Task restore;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var execution = scope.ServiceProvider.GetRequiredService<LiveAccess>();
                await scope.ServiceProvider.GetRequiredService<DetachedCommandAdmission>()
                    .AdmitAsync(execution, presented, actor.Owner);
                restore = EnsureInitializedAsync(execution, actor.Owner, page);
                if (restore.IsCompletedSuccessfully)
                    return Start(action, validate, execution, access, actor.Owner, page, initialProgress);
            }
            await restore;
            await access.RequireOwnerAsync(actor.Owner, AccessPolicies.PublishDocumentation);
            // A restore wait can outlive capability or stamp revocation. Verify
            // the original principal with a fresh account scope, not a refreshed
            // principal or the pre-wait scope's potentially tracked user.
            await using var resumed = scopes.CreateAsyncScope();
            var current = resumed.ServiceProvider.GetRequiredService<LiveAccess>();
            await resumed.ServiceProvider.GetRequiredService<DetachedCommandAdmission>().AdmitAsync(current, presented, actor.Owner);
            return Start(action, validate, current, access, actor.Owner, page, initialProgress);
        }
        finally { commandAdmission.Release(); }
    }
    private async Task ExecuteAuthorizedAsync(System.Security.Claims.ClaimsPrincipal admitted, Func<LiveAccess, Task> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        var execution = scope.ServiceProvider.GetRequiredService<LiveAccess>();
        execution.Capture(admitted);
        await execution.RequireAsync(AccessPolicies.PublishDocumentation);
        await action(execution);
    }
    private bool Start(Func<LiveAccess, Task> action, Action validate, LiveAccess access, LiveAccess circuit,
        string owner, PublishingPage page, PublishingProgress? initialProgress)
    {
        var changed = false;
        try
        {
        lock (gate)
        {
            if (circuit.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation).Owner != owner)
                throw new AccessDeniedException("not_found", 404, "This resource is not available to this account.");
            var actor = access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            var admitted = access.CapturePrincipal();
            if (busy || stopping) return false;
            try
            {
                if (phase == "Blocked") throw new PublishingBlock(error ?? "Publishing is blocked without a recoverable operation result. Keep Repository and Staging/current.json; have the application maintainer establish the retained phase and commit/ref state before any command or cleanup.");
                settings.Validate(); storage.Admit(); validate();
            }
            catch (PublishingBlock ex) { changed |= error != ex.Message; error = ex.Message; return false; }
            catch (Exception ex) { RetainFailure(ex, actor.Owner, page); changed = true; return false; }
            busy = true; reviewAdmissionVersion++; error = null; publishedUrl = null;
            progress = initialProgress; stage = initialProgress is null ? "Discarding prepared changes" : "Operation in progress";
            if (initialProgress?.Operation == "Preparation")
            {
                latestPreparation = new(DateTime.UtcNow, actor.Account,
                    actor.Bypass ? PublishingActivityService.LocalAdminLabel :
                    string.IsNullOrWhiteSpace(actor.Username) ? "Unknown account" : actor.Username);
                mapPreflight = null; mapPreparation = Guid.NewGuid();
            }
            changed = true;
            failure = null; failureOwner = null; failurePage = default;
            timings = new(logger, NotifyChanged); timingOwner = actor.Owner; timingPage = page;
            var operationTimings = timings;
            var admittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            // Do not capture a circuit cancellation token or its scoped services.
            activeJob = Task.Run(async () =>
            {
                lock (gate) { /* Wait for admission projection before diagnostic signals. */ }
                Exception? operationFailure = null;
                using var operation = operationTimings.Begin(initialProgress?.Operation ?? "Discard", started: admittedAt);
                using (var executionTiming = operationTimings.Begin("Execution"))
                {
                try { await ExecuteAuthorizedAsync(admitted, action); executionTiming.Complete(); }
                catch (Exception ex) { operationFailure = ex; }
                }
                if (taskTiming is not null)
                {
                    if (operationFailure is null) taskTiming.Complete();
                    taskTiming.Dispose(); taskTiming = null; storage.Timing = null;
                }
                using var finalization = operationTimings.Begin("Finalization");
                storage.Timing = finalization;
                try
                {
                    var failureBeforeFinalization = operationFailure;
                    lock (gate)
                    {
                        // Finalization retains primary errors exactly as before.
                        FinalizeOperation();
                    }
                    if (ReferenceEquals(failureBeforeFinalization, operationFailure)) finalization.Complete();
                    if (operationFailure is null) operation.Complete();
                }
                finally
                {
                    storage.Timing = null;
                    finalization.Dispose(); operation.Dispose();
                    lock (gate) busy = false;
                    NotifyChanged();
                }
                void FinalizeOperation()
                    {
                        // Publish the failure and stopped progress together; an already
                        // queued refresh must not see an error with a still-running task.
                        if (operationFailure is PublishingBlock block) error = block.Message;
                        else if (operationFailure is not null) RetainFailure(operationFailure, actor.Owner, page);
                        if (journal?.Phase == "Preparing")
                        {
                            journal.Phase = "Failed";
                            try { storage.Save(journal); }
                            catch (Exception ex)
                            {
                                phase = "Blocked";
                                operationFailure = operationFailure is null ? ex : new AggregateException("Publishing failed and saving recovery state also failed.", operationFailure, ex);
                                RetainFailure(operationFailure, actor.Owner, page);
                            }
                        }
                        if (journal?.Phase is "Replacing" or "Advancing") phase = "Blocked";
                        else if (phase != "Blocked") phase = journal?.Phase ?? "Idle";
                        if (phase != "Prepared") { roomReview = null; reviewError = null; }
                        else if (operationFailure is not null && initialProgress?.Operation == "Preparation")
                        {
                            // Cleanup failed before a fresh journal took ownership.
                            // Never label that new attempt's report as the old candidate's.
                            mapPreflight = null; mapPreparation = null;
                            RestorePreparationReport(journal!);
                        }
                        try { finalization.Measure(PublishingMetric.Projection, PublishIdentity); }
                        catch (Exception ex)
                        {
                            phase = "Blocked";
                            candidateId = null;
                            roomReview = null;
                            operationFailure = operationFailure is null ? ex : new AggregateException("Publishing failed and projecting recovery state also failed.", operationFailure, ex);
                            RetainFailure(operationFailure, actor.Owner, page);
                        }
                        finally
                        {
                            if (initialProgress?.Operation == "Preparation" && phase != "Prepared")
                                SettleStoppedMapPreflight(operationFailure is not null);
                            if (operationFailure is not null)
                            {
                                // Retain this process's known stopped task, never live item counts.
                                if (progress is not null) progress = progress with { RoomsCompleted = null, RoomsTotal = null, CurrentRoom = null, Detail = null };
                                stage = journal?.Phase == "Published" ? "Publication confirmed; cleanup did not finish" : "Operation stopped before completion";
                            }
                            else progress = null;
                        }
                    }
            });
            return true;
        }
        }
        finally { if (changed) NotifyChanged(); }
    }
    // Gate-owned detached projection; never retain a circuit service or page registry.
    private void RetainFailure(Exception exception, string owner, PublishingPage page)
    {
        failure = new(Guid.NewGuid(), exception.ToString());
        failureOwner = owner; failurePage = page;
        logger.LogError(exception, "Documentation publishing failed in {Phase}.", journal?.Phase ?? phase);
        error = journal?.Phase switch
        {
            "Published" => "Publication was confirmed, but cleanup did not finish. See the error details; preserve retained evidence.",
            "PendingPush" => "Push outcome is unconfirmed. The exact commit is retained for reconciliation. See the error details.",
            _ => "Publication did not finish. See the error details; preserve retained recovery evidence."
        };
    }
    private void Progress(PublishingProgress value)
    {
        lock (gate)
        {
            if (progress == value) return;
            progress = value;
        }
        NotifyChanged();
    }
    private void BeginTask(PublishingProgress value)
    {
        if (taskTiming is not null) { taskTiming.Complete(); taskTiming.Dispose(); }
        taskTiming = timings!.Begin($"{value.Operation}: task {value.Step} of {value.Steps} — {value.Task}");
        storage.Timing = taskTiming;
        Progress(value);
    }
    private async Task PrepareAsync(LiveAccess execution)
    {
        lock (gate) { journal = new() { Configuration = settings.Identity }; phase = "Preparing"; validation = []; changes = []; roomReview = null; reviewError = null; candidateId = pendingCommit = null; }
        NotifyChanged();
        BeginTask(PreparationStep(1));
        storage.Save(journal);
        using var repo = git.PrepareRepository(journal);
        journal.BaseCommit = repo.Head.Tip.Sha; storage.Save(journal);
        var database = Path.Combine(storage.Staging, "Frozen.db");
        var captures = Path.Combine(storage.Staging, "Captures");
        BeginTask(PreparationStep(2));
        var factory = await PublishingSnapshot.AcquireAsync(liveDatabase, database, captures, images, publications, execution, CancellationToken.None, taskTiming!);
        BeginTask(PreparationStep(3));
        var documents = await taskTiming!.MeasureAsync(PublishingMetric.Load, () => PublishingDocuments.LoadAsync(factory));
        timings!.Rooms(documents.Rooms.Count);
        BeginTask(PreparationStep(4));
        var catalogue = await RequirementCatalogueExportService.ExportPreparedAsync(factory, TimeProvider.System);
        BeginTask(PreparationStep(5));
        var complete = await DistributedExportService.ExportPreparedAsync(factory, TimeProvider.System, true, taskTiming!);
        if (complete is not (CompleteExported or CompleteExportInvalidLocationType))
            throw new InvalidOperationException($"Unexpected complete room catalogue export outcome: {complete?.GetType().Name ?? "null"}.");
        BeginTask(PreparationStep(6));
        var preparedGraph = await RoomGraphExportService.GeneratePreparedAsync(factory);
        BeginTask(PreparationStep(7));
        await ValidateMapAsync(execution, preparedGraph, documents);
        if (complete is not CompleteExported)
            throw new InvalidOperationException("Complete catalogue rejected a location type not rejected by canonical graph integrity.");
        BeginTask(PreparationStep(8));
        var geometry = await RoomGeometryExportService.GeneratePreparedAsync(factory);
        if (geometry.Content is null) { lock (gate) validation = [.. validation, .. geometry.Failures.Select(x => $"{x.RoomName}: {x.ChildName}: {x.Cause}")]; NotifyChanged(); throw new PublishingBlock("Room geometry export failed. Correct the listed records and prepare again."); }
        PublishingStorage.CheckTree(storage.Staging);
        Directory.CreateDirectory(storage.Candidate);
        File.Move(database, Path.Combine(storage.Candidate, "silksong-rando-logic.db"));
        storage.Atomic(Path.Combine(storage.Candidate, "silksong-requirement-catalogue.json"), catalogue.JsonUtf8);
        storage.Atomic(Path.Combine(storage.Candidate, "silksong-room-catalogue.json"), ((CompleteExported)complete).Export.JsonUtf8);
        storage.Atomic(Path.Combine(storage.Candidate, "room_graph_data.json"), preparedGraph.Export.Content!);
        storage.Atomic(Path.Combine(storage.Candidate, "room_geometry_data.json"), geometry.Content);
        BeginTask(PreparationStep(9));
        documents.Write(storage, storage.Candidate, captures, font, preparedGraph.Evidence, (room, count, maximum) =>
            Progress(PreparationStep(9) with { RoomsCompleted = count, RoomsTotal = maximum, CurrentRoom = room,
                Detail = room is null ? "Write README, MASTER and documentation indexes." : null }), timings);
        BeginTask(PreparationStep(10));
        journal.Files = taskTiming!.Measure(PublishingMetric.Hashing, () => Directory.EnumerateFiles(storage.Candidate, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToDictionary(x => Path.GetRelativePath(storage.Candidate, x).Replace('\\', '/'), PublishingStorage.FileHash, StringComparer.Ordinal));
        taskTiming.Measure(PublishingMetric.Inventory, () => git.Inventory(repo, journal));
        var lengths = taskTiming.Measure(PublishingMetric.Validation, () => storage.ValidateForReview(journal));
        var review = taskTiming.Measure(PublishingMetric.Review, () => Review(repo, journal, lengths));
        var (projection, projectionError) = BuildReview(repo, journal, review, taskTiming);
        BeginTask(PreparationStep(11));
        journal.PreparationReport = JsonSerializer.SerializeToElement(new PublishingPreparationReport(
            PublishingStorage.CandidateIdentity(journal), mapPreparation!.Value, mapPreflight!));
        journal.Phase = "Prepared"; storage.Save(journal);
        taskTiming!.Measure(PublishingMetric.Cleanup, () => { PublishingStorage.CheckTree(captures); Directory.Delete(captures, true); });
        lock (gate) { changes = review; roomReview = projection; reviewError = projectionError; phase = "Prepared"; stage = changes.Count == 0 ? "No changes; nothing to publish" : "Ready for review; not yet published"; PublishIdentity(); progress = null; }
        NotifyChanged();
    }
    private Task PushAsync()
    {
        if (!git.Push(journal!)) throw new PublishingBlock($"Remote confirmation did not establish that branch {settings.Branch} contains retained commit {journal!.Commit}. Keep that exact commit and Staging/current.json; use Retry push for this retained commit to fetch/reconcile remote history before any further push. Do not create a new commit for this unconfirmed outcome.");
        var commit = journal!.Commit;
        journal.Phase = "Published"; storage.Save(journal);
        // A durable confirmed outcome is written before cleanup. Startup never pushes.
        var url = settings.RepositoryUrl.EndsWith(".git", StringComparison.Ordinal) ? settings.RepositoryUrl[..^4] : settings.RepositoryUrl;
        taskTiming!.Measure(PublishingMetric.Cleanup, storage.Clear);
        lock (gate) { publishedUrl = url + "/commit/" + commit; stage = "Publication confirmed"; phase = "Idle"; journal = null; changes = []; mapPreflight = null; mapPreparation = null; PublishIdentity(); progress = null; }
        NotifyChanged();
        return Task.CompletedTask;
    }
    private IReadOnlyList<PublishingChange> Review(Repository repo, PublishingJournal current, IReadOnlyDictionary<string, long> lengths)
    {
        var old = PublishingGit.Blobs(repo.Lookup<Commit>(current.BaseCommit).Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var list = new List<PublishingChange>();
        foreach (var path in current.Files.Keys.Concat(current.Removed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = old.GetValueOrDefault(path); var after = current.Files.GetValueOrDefault(path);
            var beforeSize = before?.Size ?? 0;
            var afterSize = after is null ? 0 : lengths[path];
            if (before is not null && after is not null && beforeSize == afterSize)
            {
                using var content = before.GetContentStream();
                if (Convert.ToHexStringLower(SHA256.HashData(content)) == after) continue;
            }
            var image = path.EndsWith(".png", StringComparison.Ordinal) || path.EndsWith(".webp", StringComparison.Ordinal);
            list.Add(new(path, image ? "Image" : path.EndsWith(".md") ? "Documentation" : "Machine artifact",
                before is null ? "Added" : after is null ? "Removed" : "Changed", beforeSize, afterSize));
        }
        return list.ToArray();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        Task? active;
        lock (gate) { stopping = true; pendingMapDecision?.Completion.TrySetResult(MapDecisionChoice.Shutdown); active = activeJob ?? initialization; }
        // Do not release the process writer lease while admitted work can still
        // touch the checkout. Abrupt process exit instead uses durable recovery.
        if (active is not null) await active.ConfigureAwait(false);
        storage.Dispose();
    }
}
