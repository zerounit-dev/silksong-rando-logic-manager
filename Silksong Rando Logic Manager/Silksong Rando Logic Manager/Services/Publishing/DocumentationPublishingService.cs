using LibGit2Sharp;
using System.Text;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

public sealed record PublishingChange(string Path, string Group, string Kind, string Change, long BeforeBytes, long AfterBytes);
public readonly record struct PublishingPage(Guid Id);
public sealed record PublishingFailure(Guid Id, string Details);
public sealed record PublishingProgress(string Operation, int Step, int Steps, string Task,
    int? RoomsCompleted = null, int? RoomsTotal = null, string? CurrentRoom = null, string? Detail = null);
public sealed record PublishingView(bool Enabled, string Repository, string Branch, bool CredentialsReady,
    bool Busy, string Phase, string Stage, PublishingProgress? Progress, string? CandidateId,
    string? Commit, string? PublishedUrl, string? Error, IReadOnlyList<string> Validation, IReadOnlyList<PublishingChange> Changes,
    PublishingFailure? Failure, IReadOnlyList<PublishingTiming> Timings);
public sealed record PrepareDocumentation(PublishingPage Page);
public sealed record CommitDocumentation(PublishingPage Page, string CandidateId, string Message);
public sealed record RetryDocumentationPush(PublishingPage Page, string Commit);
public sealed record DiscardDocumentation(PublishingPage Page, string? CandidateId);

/// <summary>
/// Process-owned operation lifetime, independent of circuits. Security integration
/// must authorize every public command/read at execution (including after gates),
/// not just the /tools/publishing route. No ambient missing principal means Admin.
/// </summary>
public sealed class DocumentationPublishingService : IDisposable, IAsyncDisposable
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
    private bool initialized, busy, stopping;
    private Task? activeJob;
    private string stage = "Not prepared", phase = "Idle";
    private PublishingProgress? progress;
    private static readonly string[] PreparationTasks =
    [
        "Check the publishing repository and download remote changes if needed.",
        "Copy a consistent database snapshot and the available room images.",
        "Load room records and check that they are ready for publication.",
        "Export the requirement catalogue used by the published logic.",
        "Export the complete room catalogue.",
        "Export the connections and logic linking rooms.",
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
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<SharedPublicationCoordinator>()));
    }

    internal DocumentationPublishingService(PublishingSettings settings, IWebHostEnvironment environment, ApplicationStoragePaths paths, SceneImageFileService images,
        ILogger<DocumentationPublishingService> logger, IServiceScopeFactory scopes, SharedPublicationCoordinator publications)
    {
        this.settings = settings;
        storage = new(paths.DataRoot); git = new(storage, settings); this.images = images;
        liveDatabase = paths.LogicDatabase;
        font = Path.Combine(environment.ContentRootPath, "Assets", "Publishing", "NotoSans-Bold.ttf");
        this.logger = logger;
        this.scopes = scopes;
        this.publications = publications;
    }
    public async Task<PublishingView> ReadAsync(LiveAccess access, PublishingPage page, Guid? timingOperation = null, long timingSequence = 0)
    {
        await access.RequireAsync(AccessPolicies.PublishDocumentation);
        lock (gate)
        {
            var actor = access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            Initialize(actor.Owner, page);
            // Invalid configuration must not echo a URL with embedded credentials.
            var safeUrl = "Not configured";
            try { settings.Validate(); safeUrl = settings.RepositoryUrl; } catch (PublishingBlock) { }
            return new(settings.Enabled, safeUrl, settings.Branch, settings.CredentialsReady, busy, phase, stage, progress,
                CandidateId(), pendingCommit, publishedUrl, error, validation, changes,
                failureOwner == actor.Owner && failurePage == page ? failure : null,
                timingOwner == actor.Owner && timingPage == page ? timings?.Read(timingOperation, timingSequence) ?? [] : []);
        }
    }
    private void Initialize(string owner, PublishingPage page)
    {
        if (initialized) return;
        initialized = true;
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
        }
    }
    private void InitializeJournal()
    {
        settings.Validate(); storage.Admit(); journal = storage.Load();
        if (journal is null) return;
        if (journal.Configuration != settings.Identity) throw new PublishingBlock("Publishing configuration differs from retained operation evidence. Preserve the workspace for operator recovery.");
        if (journal.Phase == "Published") { storage.Clear(); journal = null; stage = "Previous publication was confirmed; prepare a new candidate"; return; }
        if (journal.Phase is "Replacing" or "Advancing") git.Recover(journal);
        if (journal.Phase == "Preparing") { journal.Phase = "Interrupted"; storage.Save(journal); }
        if (journal.Phase == "Prepared")
        {
            storage.Validate(journal);
            if (journal.Renderer != PublishingRenderer.Version) throw new PublishingBlock("The retained candidate uses a different renderer version. Discard/reprepare and explicitly review the renderer change.");
            using var repo = git.Open(); git.EnsureClean(repo);
            if (repo.Head.Tip.Sha != journal.BaseCommit) throw new PublishingBlock("Prepared base HEAD changed. Discard and prepare again.");
            changes = Review(repo, journal);
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
    // Called under gate only at initialization or a quiescent operation boundary,
    // never from ordinary status/progress reads. No manifest/list reference escapes.
    private void PublishIdentity()
    {
        candidateId = journal is null || phase == "Blocked" ? null : PublishingStorage.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { journal.Configuration, journal.BaseCommit, journal.Renderer, Files = journal.Files.OrderBy(x => x.Key, StringComparer.Ordinal), journal.Removed }));
        pendingCommit = journal?.Phase == "PendingPush" ? journal.Commit : null;
    }
    public Task<bool> PrepareAsync(LiveAccess access, PrepareDocumentation command) => StartAuthorizedAsync(access, command.Page, PreparationStep(1), execution => PrepareAsync(execution), () =>
    {
        if (journal is not null) throw new PublishingBlock("Discard the current safe candidate before preparing again. Pending/uncertain commit evidence must first be reconciled.");
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
        if (journal?.Phase != "Prepared" || command.CandidateId != CandidateId()) throw new PublishingBlock("Review the current prepared candidate before committing.");
        if (changes.Count == 0) throw new PublishingBlock("No changes; no empty commit will be created.");
    });
    public Task<bool> RetryPushAsync(LiveAccess access, RetryDocumentationPush command) => StartAuthorizedAsync(access, command.Page, RetryStep(), _ => { BeginTask(RetryStep()); return PushAsync(); }, () =>
    {
        if (journal?.Phase != "PendingPush" || journal.Commit != command.Commit) throw new PublishingBlock("Retry must name the current pending commit.");
    });
    public Task<bool> DiscardAsync(LiveAccess access, DiscardDocumentation command) => StartAuthorizedAsync(access, command.Page, null, _ =>
    {
        using var discard = timings!.Begin("Discard prepared changes");
        storage.Timing = discard;
        try
        {
        discard.Measure(PublishingMetric.Cleanup, storage.Clear); lock (gate) { journal = null; changes = []; validation = []; phase = "Idle"; stage = "Discarded"; progress = null; PublishIdentity(); }
        NotifyChanged();
        discard.Complete();
        return Task.CompletedTask;
        }
        finally { storage.Timing = null; }
    }, () =>
    {
        if (journal?.Phase is not ("Prepared" or "Interrupted" or "Failed") || command.CandidateId != CandidateId() || phase == "Blocked")
            throw new PublishingBlock("Discard is blocked while rollback/commit evidence is unresolved. Preserve the workspace for operator reconciliation.");
    });
    private async Task<bool> StartAuthorizedAsync(LiveAccess access, PublishingPage page, PublishingProgress? initialProgress, Func<LiveAccess, Task> action, Action validate)
    {
        var actor = await access.RequireAsync(AccessPolicies.PublishDocumentation);
        var presented = access.CapturePrincipal();
        await commandAdmission.WaitAsync();
        try
        {
            await access.RequireOwnerAsync(actor.Owner, AccessPolicies.PublishDocumentation);
            await using var scope = scopes.CreateAsyncScope();
            var execution = scope.ServiceProvider.GetRequiredService<LiveAccess>();
            await scope.ServiceProvider.GetRequiredService<DetachedCommandAdmission>()
                .AdmitAsync(execution, presented, actor.Owner);
            // Admission precedes Initialize (which can recover journal/files),
            // command validation, and every eventual credential release.
            return Start(action, validate, execution, page, initialProgress);
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
    private bool Start(Func<LiveAccess, Task> action, Action validate, LiveAccess access, PublishingPage page, PublishingProgress? initialProgress)
    {
        var changed = false;
        try
        {
        lock (gate)
        {
            var actor = access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            var admitted = access.CapturePrincipal();
            changed = !initialized;
            Initialize(actor.Owner, page); if (busy || stopping) return false;
            try
            {
                if (phase == "Blocked") throw new PublishingBlock(error ?? "Publishing requires operator recovery. Preserve the workspace and retained evidence.");
                settings.Validate(); storage.Admit(); validate();
            }
            catch (PublishingBlock ex) { changed |= error != ex.Message; error = ex.Message; return false; }
            catch (Exception ex) { RetainFailure(ex, actor.Owner, page); changed = true; return false; }
            busy = true; error = null; publishedUrl = null;
            progress = initialProgress; stage = initialProgress is null ? "Discarding prepared changes" : "Operation in progress";
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
                        try { finalization.Measure(PublishingMetric.Projection, PublishIdentity); }
                        catch (Exception ex)
                        {
                            phase = "Blocked";
                            candidateId = null;
                            operationFailure = operationFailure is null ? ex : new AggregateException("Publishing failed and projecting recovery state also failed.", operationFailure, ex);
                            RetainFailure(operationFailure, actor.Owner, page);
                        }
                        finally
                        {
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
        lock (gate) { journal = new() { Configuration = settings.Identity }; phase = "Preparing"; validation = []; changes = []; candidateId = pendingCommit = null; }
        NotifyChanged();
        BeginTask(PreparationStep(1));
        storage.Save(journal);
        using var repo = git.PrepareRepository(journal);
        journal.BaseCommit = repo.Head.Tip.Sha; storage.Save(journal);
        Directory.CreateDirectory(storage.Candidate);
        var database = Path.Combine(storage.Candidate, "silksong-rando-logic.db");
        var captures = Path.Combine(storage.Staging, "Captures");
        BeginTask(PreparationStep(2));
        var factory = await PublishingSnapshot.AcquireAsync(liveDatabase, database, captures, images, publications, execution, CancellationToken.None, taskTiming!);
        BeginTask(PreparationStep(3));
        var documents = await taskTiming!.MeasureAsync(PublishingMetric.Load, () => PublishingDocuments.LoadAsync(factory));
        timings!.Rooms(documents.Rooms.Count);
        var issues = taskTiming.Measure(PublishingMetric.Validation, documents.Validate);
        lock (gate) validation = issues.ToArray();
        NotifyChanged();
        if (issues.Count != 0) throw new PublishingBlock("Publication validation failed. Correct the listed records and prepare a fresh candidate; no authored data was repaired.");
        BeginTask(PreparationStep(4));
        var catalogue = await RequirementCatalogueExportService.ExportPreparedAsync(factory, TimeProvider.System);
        storage.Atomic(Path.Combine(storage.Candidate, "silksong-requirement-catalogue.json"), catalogue.JsonUtf8);
        BeginTask(PreparationStep(5));
        var complete = await DistributedExportService.ExportPreparedAsync(factory, TimeProvider.System, true);
        if (complete is not CompleteExported exported) throw new PublishingBlock("The complete room catalogue could not be exported. Correct its location-type diagnostics and prepare again.");
        storage.Atomic(Path.Combine(storage.Candidate, "silksong-room-catalogue.json"), exported.Export.JsonUtf8);
        BeginTask(PreparationStep(6));
        var graph = await RoomGraphExportService.GeneratePreparedAsync(factory);
        if (graph.Content is null) { lock (gate) validation = graph.Failures.Select(x => $"{x.RoomName}: {x.ChildName}: {x.Cause}").ToArray(); NotifyChanged(); throw new PublishingBlock("Room graph export failed. Correct the listed records and prepare again."); }
        storage.Atomic(Path.Combine(storage.Candidate, "room_graph_data.json"), graph.Content);
        BeginTask(PreparationStep(7));
        var geometry = await RoomGeometryExportService.GeneratePreparedAsync(factory);
        if (geometry.Content is null) { lock (gate) validation = geometry.Failures.Select(x => $"{x.RoomName}: {x.ChildName}: {x.Cause}").ToArray(); NotifyChanged(); throw new PublishingBlock("Room geometry export failed. Correct the listed records and prepare again."); }
        storage.Atomic(Path.Combine(storage.Candidate, "room_geometry_data.json"), geometry.Content);
        BeginTask(PreparationStep(8));
        documents.Write(storage, storage.Candidate, captures, font, (room, count, maximum) =>
            Progress(PreparationStep(8) with { RoomsCompleted = count, RoomsTotal = maximum, CurrentRoom = room,
                Detail = room is null ? "Write README, MASTER and documentation indexes." : null }), timings);
        BeginTask(PreparationStep(9));
        journal.Files = taskTiming!.Measure(PublishingMetric.Hashing, () => Directory.EnumerateFiles(storage.Candidate, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToDictionary(x => Path.GetRelativePath(storage.Candidate, x).Replace('\\', '/'), PublishingStorage.FileHash, StringComparer.Ordinal));
        taskTiming.Measure(PublishingMetric.Inventory, () => git.Inventory(repo, journal));
        taskTiming.Measure(PublishingMetric.Validation, () => storage.Validate(journal));
        var review = taskTiming.Measure(PublishingMetric.Review, () => Review(repo, journal));
        BeginTask(PreparationStep(10));
        journal.Phase = "Prepared"; storage.Save(journal);
        taskTiming!.Measure(PublishingMetric.Cleanup, () => { PublishingStorage.CheckTree(captures); Directory.Delete(captures, true); });
        lock (gate) { changes = review; phase = "Prepared"; stage = changes.Count == 0 ? "No changes; nothing to publish" : "Ready for review; not yet published"; PublishIdentity(); progress = null; }
        NotifyChanged();
    }
    private Task PushAsync()
    {
        if (!git.Push(journal!)) throw new PublishingBlock("Remote confirmation was inconclusive. Retry this exact pending commit.");
        var commit = journal!.Commit;
        journal.Phase = "Published"; storage.Save(journal);
        // A durable confirmed outcome is written before cleanup. Startup never pushes.
        var url = settings.RepositoryUrl.EndsWith(".git", StringComparison.Ordinal) ? settings.RepositoryUrl[..^4] : settings.RepositoryUrl;
        taskTiming!.Measure(PublishingMetric.Cleanup, storage.Clear);
        lock (gate) { publishedUrl = url + "/commit/" + commit; stage = "Publication confirmed"; phase = "Idle"; journal = null; changes = []; PublishIdentity(); progress = null; }
        NotifyChanged();
        return Task.CompletedTask;
    }
    private IReadOnlyList<PublishingChange> Review(Repository repo, PublishingJournal current)
    {
        var old = PublishingGit.Blobs(repo.Lookup<Commit>(current.BaseCommit).Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var list = new List<PublishingChange>();
        foreach (var path in current.Files.Keys.Concat(current.Removed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = old.GetValueOrDefault(path); var after = current.Files.GetValueOrDefault(path);
            if (before is not null && after is not null && PublishingStorage.Hash(PublishingGit.Bytes(before)) == after) continue;
            var image = path.EndsWith(".png", StringComparison.Ordinal) || path.EndsWith(".webp", StringComparison.Ordinal);
            var group = path.StartsWith("00-annotations/") ? path.Split('/')[1] : path.Contains('/') && path.EndsWith(".md") ? path.Split('/')[0] : "Machine artifacts / publication index";
            list.Add(new(path, group, image ? "Image" : path.EndsWith(".md") ? "Documentation" : "Machine artifact",
                before is null ? "Added" : after is null ? "Removed" : "Changed", before?.Size ?? 0,
                after is null ? 0 : new FileInfo(PublishingStorage.Resolve(storage.Candidate, path)).Length));
        }
        return list.ToArray();
    }
    public async Task<string> ReadTextDiffAsync(LiveAccess access, PublishingPage page, string candidateId, string path, PublishingTimings? requestTimings = null)
    {
        using var timing = (requestTimings ?? CreatePreviewTimings()).Begin("Text preview service");
        await timing.MeasureAsync(PublishingMetric.Authorization, () => access.RequireAsync(AccessPolicies.PublishDocumentation));
        using var gateWait = timing.Segment(PublishingMetric.GateWait);
        lock (gate)
        {
            gateWait.Complete();
            var actor = access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            Initialize(actor.Owner, page); if (busy || journal?.Phase != "Prepared" || candidateId != CandidateId() || !changes.Any(x => x.Path == path && x.Kind != "Image") || !(path.EndsWith(".md") || path.EndsWith(".json")))
                throw new PublishingBlock("Only current reviewed text artifacts can be previewed.");
            timing.Measure(PublishingMetric.Validation, () => storage.Validate(journal));
            string response;
            using (var repo = timing.Measure(PublishingMetric.GitOpen, git.Open))
            {
            var blob = repo.Lookup<Commit>(journal.BaseCommit).Tree[path]?.Target as Blob;
            var before = blob is null ? "" : timing.Measure(PublishingMetric.BeforeRead, () => Encoding.UTF8.GetString(PublishingGit.Bytes(blob)));
            var after = journal.Files.ContainsKey(path) ? timing.Measure(PublishingMetric.AfterRead, () => File.ReadAllText(PublishingStorage.Resolve(storage.Candidate, path))) : "";
            response = timing.Measure(PublishingMetric.ResponseBuild, () =>
            {
                var beforeLines = before.Split('\n'); var afterLines = after.Split('\n');
                timing.BeforeLines = beforeLines.LongLength; timing.AfterLines = afterLines.LongLength;
                return "--- publication HEAD/" + path + "\n+++ candidate/" + path + "\n" +
                    string.Join("\n", beforeLines.Select(x => "-" + x)) + "\n" + string.Join("\n", afterLines.Select(x => "+" + x));
            });
            timing.InputBytes = Encoding.UTF8.GetByteCount(before) + (long)Encoding.UTF8.GetByteCount(after);
            timing.OutputBytes = Encoding.UTF8.GetByteCount(response);
            }
            timing.Complete(); return response;
        }
    }
    public async Task<string> ReadImageAsync(LiveAccess access, PublishingPage page, string candidateId, string path, PublishingTimings? requestTimings = null)
    {
        using var timing = (requestTimings ?? CreatePreviewTimings()).Begin("Image preview service");
        await timing.MeasureAsync(PublishingMetric.Authorization, () => access.RequireAsync(AccessPolicies.PublishDocumentation));
        using var gateWait = timing.Segment(PublishingMetric.GateWait);
        lock (gate)
        {
            gateWait.Complete();
            var actor = access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            Initialize(actor.Owner, page); if (busy || journal?.Phase != "Prepared" || candidateId != CandidateId() || !journal.Files.ContainsKey(path) ||
                !changes.Any(x => x.Path == path && x.Kind == "Image")) throw new PublishingBlock("Only current manifest-owned changed images can be previewed.");
            timing.Measure(PublishingMetric.Validation, () => storage.Validate(journal));
            var bytes = timing.Measure(PublishingMetric.AfterRead, () => File.ReadAllBytes(PublishingStorage.Resolve(storage.Candidate, path)));
            timing.InputBytes = bytes.LongLength;
            var response = timing.Measure(PublishingMetric.ResponseBuild, () => "data:image/" + (path.EndsWith(".png") ? "png" : "webp") + ";base64," + Convert.ToBase64String(bytes));
            timing.OutputBytes = response.Length;
            timing.Complete(); return response;
        }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        Task? active;
        lock (gate) { stopping = true; active = activeJob; }
        // Do not release the process writer lease while admitted work can still
        // touch the checkout. Abrupt process exit instead uses durable recovery.
        if (active is not null) await active.ConfigureAwait(false);
        storage.Dispose();
    }
}
