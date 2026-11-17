using System.Net.Http.Json;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Pages;

public partial class Requirements
{
    [Parameter] public string? Kind { get; set; }
    [CascadingParameter] public App Shell { get; set; } = default!;
    [Inject] public ConfirmedCatalogue Catalogue { get; set; } = default!;
    [Inject] public SessionAccess Access { get; set; } = default!;
    [Inject] public LogicBrowserCache Browser { get; set; } = default!;
    [Inject] public ClientRuntime Runtime { get; set; } = default!;
    private static readonly string[] Kinds = ["predicates", "crests", "methods", "items", "countables", "souls"];
    private static readonly string[] Labels = ["Predicates", "Crests", "Methods", "Items", "Countables", "Souls"];
    private static readonly string[] Singular = ["predicate", "crest", "method", "item", "countable", "soul"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly string[] searches = new string[6];
    private ImmutableList<ImmutableList<Row>>? lists;
    private List<Row>? provisional;
    private ConfirmedManager? displayed;
    private int active, routeKind, pickerGeneration;
    private long generation, mutationEpoch;
    private bool disposed, busy, readPending, freshQueued, forceQueued, loadFailed, showDialog, restoreFocus;
    private Task? readFlight;
    private string? modal, pending, feedback, modalFeedback, importText;
    private IReadOnlyList<RequirementCatalogueValidationIssue> remoteIssues = [];
    private RequirementRecheckCounts? counts;
    private Draft? draft;
    private RequirementCatalogueReferenceView? guide;
    private Row? deleteRow;
    private Guid? dragged, moving, dropTarget;
    private bool dropTail;
    private bool routeApplied;
    private string? appliedKind;
    private ElementReference root, dialog;
    private IJSObjectReference? module, owner;
    private DotNetObjectReference<Requirements>? callback;
    private bool ClientUpdateWorkAtRisk() => busy || dragged is not null
        || provisional is not null || modal is "edit" or "delete" or "import";

    private sealed record Row(Guid Id, string Name, string? Category, string? InputSyntax, string Output, string Aliases, string Notes, IReadOnlyList<string> ParsedAliases);
    private sealed class Draft
    {
        public Guid? Id; public string Name = "", Aliases = "", Output = "", Notes = "", Syntax = "{predicate}";
        public string? Category; public bool AliasesTouched;
    }
    private bool ActionsBlocked => busy || modal is not null || dragged is not null || !Shell.CanUseRequirements(this);
    private bool AuthoringActionsBlocked => !Shell.CanAuthor || ActionsBlocked;
    private bool Filtered => !string.IsNullOrWhiteSpace(searches[active]);
    private bool CanReorder => Shell.CanAuthor && !busy && modal is null && !Filtered && Shell.CanUseRequirements(this);
    private IEnumerable<Row> VisibleRows => (provisional is { } movingRows ? movingRows : (IEnumerable<Row>)lists![active]).Where(row => !Filtered || new[] { row.Name, row.Category, row.Aliases }.Any(x => x?.Contains(searches[active], StringComparison.OrdinalIgnoreCase) == true));
    private IEnumerable<string> Categories => lists![active].Select(x => x.Category).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    private string DialogTitle => modal == "edit" ? $"{Singular[active]} definition" : modal == "delete" ? "delete definition?" : "replace catalogue?";
    private ApplyRequirementPredicate PredicateDraft => new(draft!.Id, draft.Name, draft.Category, draft.Syntax, draft.Aliases, draft.Output, draft.Notes);
    private ApplyRequirementItem LiteralDraft => new(draft!.Id, draft.Name, draft.Category, draft.Aliases, draft.Output, draft.Notes);
    private IReadOnlyList<RequirementCatalogueValidationIssue> DraftIssues => draft is null ? [] : active == 0 ? RequirementCatalogueValidator.ValidateDraft(PredicateDraft).Issues : RequirementCatalogueValidator.ValidateDraft(LiteralDraft).Issues;
    private bool DraftValid => draft is not null && DraftIssues.Count == 0;
    private RenderFragment Icon(bool spin, string icon) => builder =>
    {
        builder.OpenElement(0, "span"); builder.AddAttribute(1, "class", "v3-icon-slot"); builder.AddAttribute(2, "aria-hidden", "true");
        builder.OpenElement(3, "i"); builder.AddAttribute(4, "class", "fa-solid " + (spin ? "fa-spinner fa-spin" : icon)); builder.CloseElement(); builder.CloseElement();
    };
    private RenderFragment LocalIssues(string field) => builder =>
    {
        foreach (var issue in DraftIssues.Where(x => x.Field == field))
        { builder.OpenElement(0, "span"); builder.AddAttribute(1, "class", "danger rc-validation"); builder.AddContent(2, issue.Message); builder.CloseElement(); }
    };
    private static string IssueText(RequirementCatalogueValidationIssue issue) =>
        $"{issue.DefinitionName ?? issue.DefinitionId.ToString()} / {issue.Field}: {issue.Message}" +
        (issue.CollidingDefinitionId is { } id ? $" Conflicts with {issue.CollidingDefinitionName ?? id.ToString()} ({id})." : "");

    protected override void OnInitialized()
    {
        Shell.RegisterClientUpdateRisk(this, ClientUpdateWorkAtRisk);
        Array.Fill(searches, "");
        Catalogue.Changed += CatalogueChanged;
        if (Shell.TakeInitialRequirements() is { } initial) Install(initial);
        else if (Catalogue.Manager is { } current) Install(current);
    }

    internal static int RouteKind(string? kind) => kind is null ? 0 : Array.FindIndex(Kinds, x => string.Equals(x, kind, StringComparison.OrdinalIgnoreCase));
    internal static bool IsManagerRoute(string path)
    {
        var parts = path.Split('?', '#')[0].Trim('/').Split('/');
        return parts is ["requirements"] || parts is ["requirements", var kind] && RouteKind(kind) >= 0;
    }
    protected override void OnParametersSet()
    {
        if (routeApplied && string.Equals(appliedKind, Kind, StringComparison.Ordinal)) return;
        routeApplied = true; appliedKind = Kind;
        routeKind = RouteKind(Kind);
        ClearDrag();
        if (routeKind >= 0) active = routeKind;
        feedback = null;
    }
    public override Task SetParametersAsync(ParameterView parameters)
    {
        // The bare route has no Kind entry when the router reuses this instance.
        if (!parameters.TryGetValue<string?>(nameof(Kind), out _)) Kind = null;
        return base.SetParametersAsync(parameters);
    }

    private void CatalogueChanged()
    {
        if (disposed || ReferenceEquals(displayed?.Common, Catalogue.Vocabulary)) return;
        confirmedPending = true;
        if (!BlockedRead && !readPending && Catalogue.Manager is { } current && ReferenceEquals(current.Common, Catalogue.Vocabulary)) Install(current);
        _ = InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Runtime.Retired) return;
        if (firstRender)
        {
            try
            {
                var imported = await ApplicationModuleLoader.ImportAsync(JS, Runtime, "requirements.js");
                if (disposed || Runtime.Retired) { await imported.DisposeAsync(); return; }
                module = imported; callback = DotNetObjectReference.Create(this);
                var mounted = await module.InvokeAsync<IJSObjectReference>("mount", callback, dialog);
                if (disposed || Runtime.Retired) { await mounted.InvokeVoidAsync("dispose"); await mounted.DisposeAsync(); return; }
                owner = mounted;
                if (lists is null && routeKind >= 0) await RequestFreshnessAsync(true);
            }
            catch (Exception exception) { if (!disposed) { loadFailed = lists is null; await ErrorAsync(exception, "The requirement catalogue could not be loaded."); StateHasChanged(); } }
        }
        if (disposed || Runtime.Retired || owner is null) return;
        try
        {
            await owner.InvokeVoidAsync("connect", routeKind >= 0 && lists is not null ? root : default(ElementReference));
            if (lists is null && routeKind >= 0 && !loadFailed && !readPending) await RequestFreshnessAsync(true);
            await owner.InvokeVoidAsync("freeze", busy);
            if (showDialog && modal == "guide" && !Shell.GeneralErrorOpen)
            {
                var guideGeneration = generation;
                var shown = await owner.InvokeAsync<bool>("showGuide", "rc-syntax-guide");
                if (!disposed && generation == guideGeneration && modal == "guide" && shown) showDialog = false;
            }
            if (showDialog && (modal is "edit" or "delete" or "import") && !Shell.GeneralErrorOpen)
            {
                var shown = await owner.InvokeAsync<bool>("show");
                if (!disposed && shown) showDialog = false;
            }
            if (restoreFocus && !Shell.GeneralErrorOpen)
            { restoreFocus = false; await owner.InvokeVoidAsync("restore"); }
            if (confirmedPending && !BlockedRead && !readPending && Catalogue.Manager is { } current) Install(current);
            if (freshQueued && !BlockedRead && !readPending) await RequestFreshnessAsync(false);
        }
        catch (Exception exception) { if (!disposed) await ErrorAsync(exception, "The catalogue interaction could not be completed. Your current input is retained."); }
    }

    private bool BlockedRead => busy || dragged is not null || modal is not null || routeKind < 0 || Shell.GeneralErrorOpen || !Shell.CanUseRequirements(this);
    private bool confirmedPending;
    private Task RequestFreshnessAsync(bool force)
    {
        if (disposed) return Task.CompletedTask;
        freshQueued = true; forceQueued |= force;
        if (BlockedRead || readPending) return readFlight ?? Task.CompletedTask;
        return readFlight = ReadLoopAsync();
    }
    private async Task ReadLoopAsync()
    {
        readPending = true; StateHasChanged();
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                using var timing = OperationTiming.Start("requirements.refresh.combined-read-decode-preparation");
                var force = forceQueued; forceQueued = freshQueued = false;
                var readGeneration = ++generation; var epoch = mutationEpoch;
                try
                {
                    await Catalogue.RefreshAsync(force);
                    if (disposed) return;
                    if (BlockedRead || readGeneration != generation || epoch != mutationEpoch) { freshQueued = true; forceQueued |= force; if (BlockedRead) break; else continue; }
                    if (Catalogue.Manager is { } current && ReferenceEquals(current.Common, Catalogue.Vocabulary))
                    { Install(current); timing.End(); if (!freshQueued) break; else continue; }
                    throw new JsonException("The initialized requirement definitions are unavailable.");
                }
                catch (Exception exception)
                {
                    if (!disposed && readGeneration == generation && epoch == mutationEpoch)
                    { loadFailed = lists is null; await ErrorAsync(exception, "The requirement catalogue could not be refreshed. The last usable view is retained."); }
                    break;
                }
            }
        }
        finally { readPending = false; if (!disposed) StateHasChanged(); }
    }
    private async Task<ReceivedCacheResult<RequirementOperationResult>> PostAsync(string suffix, object input, ClientRuntime.MutationObservation? outcome = null)
    {
        using var timing = OperationTiming.Start("requirements.buffered-send");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/requirements/" + suffix) { Content = JsonContent.Create(input, input.GetType(), options: Json) };
        var cacheRequest = Browser.Bind(request);
        outcome?.Dispatched();
        using var response = await Http.SendAsync(request, lifetime.Token);
        timing.End();
        response.EnsureSuccessStatusCode();
        using var decodeTiming = OperationTiming.Start("requirements.decode", parent: timing.Id);
        var result = await LogicBrowserCache.DecodeAsync<RequirementOperationResult>(response, cacheRequest, lifetime.Token);
        try { ValidateResult(result.Result); outcome?.OutcomeKnown(); }
        catch
        {
            // This decoded envelope cannot reach a receiving caller's finally.
            // Feature validation failure is not permission to lose independent
            // confirmed-cache evidence or to retry the business command.
            Browser.Publish(result);
            throw;
        }
        decodeTiming.End(TimingOutcome.ResponseDecoded); return result;
    }
    private void Install(ConfirmedManager value)
    {
        if (Runtime.Retired) return;
        using var timing = OperationTiming.Start("requirements.view-preparation-install");
        // Details and lists are projections of the same confirmed definitions.
        lists = value.Common.Kinds.Select((kind, index) => kind.Select(definition =>
        {
            var x = definition.Value; var detail = value.Details[index][x.Id];
            return new Row(x.Id, x.Name, x.Category, x.InputSyntax, detail.Output, x.Aliases, detail.Notes, definition.ParsedAliases);
        }).ToImmutableList()).ToImmutableList();
        // Local adoption does not consume an explicit Refresh queued while pinned.
        // ReadLoopAsync owns consumption of that obligation when a read starts.
        displayed = value; provisional = null; loadFailed = false; confirmedPending = false;
        timing.End();
    }
    public static void ValidateSnapshot(RequirementManagerSnapshot value)
    {
        if (value?.Catalogue is not { } c) throw new JsonException();
        ValidateRevision(value.Token);
        if (c.Predicates is null || c.Crests is null || c.Methods is null || c.Items is null || c.Countables is null || c.Souls is null) throw new JsonException();
        // Validate transport usability, not domain configuration: stale saved syntax
        // and invalid configuration must stay visible for deliberate correction.
        var rows = c.Predicates.Select(x => (x.Id, x.Name, x.Notes, x.Aliases, x.ParsedAliases, Output: x.OutputSyntax, Syntax: x.InputSyntax))
            .Concat(c.Crests.Select(x => (x.Id, x.Name, x.Notes, x.Aliases, x.ParsedAliases, Output: x.OutputValue, Syntax: "")))
            .Concat(c.Methods.Select(x => (x.Id, x.Name, x.Notes, x.Aliases, x.ParsedAliases, Output: x.OutputValue, Syntax: "")))
            .Concat(c.Items.Select(x => (x.Id, x.Name, x.Notes, x.Aliases, x.ParsedAliases, Output: x.OutputValue, Syntax: "")))
            .Concat(c.Countables.Select(x => (x.Id, x.Name, x.Notes, x.Aliases, x.ParsedAliases, Output: x.OutputValue, Syntax: "")))
            .Concat(c.Souls.Select(x => (x.Id, x.Name, x.Notes, x.Aliases, x.ParsedAliases, Output: x.OutputValue, Syntax: ""))).ToArray();
        if (rows.Any(x => x.Id == Guid.Empty || x.Name is null || x.Notes is null || x.Aliases is null || x.ParsedAliases is null || x.ParsedAliases.Any(y => y is null) || x.Output is null || x.Syntax is null)) throw new JsonException("Unusable catalogue snapshot.");
        foreach (var ids in new[] { c.Predicates.Select(x => x.Id), c.Crests.Select(x => x.Id), c.Methods.Select(x => x.Id), c.Items.Select(x => x.Id), c.Countables.Select(x => x.Id), c.Souls.Select(x => x.Id) })
            if (ids.Distinct().Count() != ids.Count()) throw new JsonException("Duplicate catalogue row identity.");
    }
    private static void ValidateRevision(DateTime value)
    { if (value.Kind != DateTimeKind.Utc || value.Ticks == 0) throw new JsonException(); }
    private static void ValidateResult(RequirementOperationResult value)
    {
        if (value.Status is not ("committed" or "validationRejected" or "missing" or "failed") || value.Issues is null || value.Issues.Any(x => x is null || string.IsNullOrWhiteSpace(x.Field) || string.IsNullOrWhiteSpace(x.Message))
            || value.Status == "validationRejected" && value.Issues.Count == 0 && string.IsNullOrWhiteSpace(value.Message)
            || value.Status is "missing" or "failed" && string.IsNullOrWhiteSpace(value.Message)) throw new JsonException("Unusable catalogue outcome.");
    }

    private void SearchChanged(ChangeEventArgs args) { if (!Shell.CanInteract) return; searches[active] = args.Value?.ToString() ?? ""; ClearDrag(); }
    private void NameChanged(ChangeEventArgs args)
    { if (!Shell.CanAuthor || draft is null) return; draft.Name = args.Value?.ToString() ?? ""; if (draft.Id is null && !draft.AliasesTouched) draft.Aliases = RequirementCatalogueLanguage.NormalizeIdentification(draft.Name); }
    private void ChangeDraft(Action<Draft> change) { if (Shell.CanAuthor && draft is not null) change(draft); }
    private void AliasesChanged(ChangeEventArgs args) { if (!Shell.CanAuthor || draft is null) return; draft.AliasesTouched = true; draft.Aliases = args.Value?.ToString() ?? ""; }
    private async Task OpenGuideAsync()
    {
        if (disposed || ActionsBlocked || owner is null || !Shell.LockRequirements(this)) return;
        using var timing = OperationTiming.Start("requirements.guide.read-prepare");
        var launch = ++generation;
        modal = "guide-loading";
        bool OwnsLaunch() => !disposed && generation == launch && modal == "guide-loading" && Shell.CanUseRequirements(this);
        try
        {
            await owner.InvokeVoidAsync("remember", "rc-guide-launcher");
            if (!OwnsLaunch()) return;
            var reference = await Catalogue.LoadReferenceAsync(OwnsLaunch, lifetime.Token);
            if (!OwnsLaunch() || reference is null) return;
            guide = reference; modal = "guide"; showDialog = true;
            timing.End();
        }
        catch (Exception exception)
        {
            if (OwnsLaunch()) await ErrorAsync(exception, "The syntax guide could not be loaded. Your current catalogue view is retained.");
        }
        finally
        {
            if (!disposed && generation == launch && modal == "guide-loading")
            {
                ClearModal();
                Shell.ReleaseRequirements(this);
            }
        }
    }
    private async Task OpenEditorAsync(Row? row)
    {
        if (AuthoringActionsBlocked || !Shell.LockRequirements(this)) return;
        generation++; ClearDrag(); modal = "edit"; remoteIssues = []; modalFeedback = null;
        draft = row is null ? new() : new() { Id = row.Id, Name = row.Name, Category = row.Category, Syntax = row.InputSyntax ?? "{predicate}", Aliases = row.Aliases, Output = row.Output, Notes = row.Notes, AliasesTouched = true };
        await PrepareDialogAsync();
    }
    private async Task OpenDeleteAsync(Row row)
    {
        if (AuthoringActionsBlocked || !Shell.LockRequirements(this)) return;
        generation++; ClearDrag(); modal = "delete"; deleteRow = row; remoteIssues = []; modalFeedback = null;
        await PrepareDialogAsync();
    }
    private async Task PrepareDialogAsync()
    {
        showDialog = true;
        try { if (owner is not null) await owner.InvokeVoidAsync("remember"); }
        catch (Exception exception) { await ErrorAsync(exception, "The catalogue dialog could not be opened. Your input is retained."); }
    }
    [JSInvokable] public Task CloseRequirementsDialogAsync() => CloseModalAsync();
    private async Task CloseModalAsync()
    {
        if (!Shell.CanInteract) return;
        if (disposed || busy || Shell.GeneralErrorOpen || modal is null or "guide-loading") return;
        var wasGuide = modal == "guide";
        ClearModal();
        try { if (!wasGuide && owner is not null) await owner.InvokeVoidAsync("close"); }
        catch (Exception exception) { await ErrorAsync(exception, "The catalogue dialog could not be closed."); }
        finally { Shell.ReleaseRequirements(this); }
        if (confirmedPending && !BlockedRead && !readPending && Catalogue.Manager is { } current) Install(current);
        if (freshQueued && !BlockedRead) await RequestFreshnessAsync(false);
    }
    private void ClearModal()
    { modal = null; guide = null; draft = null; deleteRow = null; importText = null; modalFeedback = null; remoteIssues = []; showDialog = false; restoreFocus = true; }
    private bool Begin(string action)
    {
        if (!Shell.CanAuthor && action != "export") return false;
        if (disposed || busy || !Shell.LockRequirements(this)) return false;
        busy = true; pending = action; mutationEpoch++; generation++; ClearDrag(); feedback = null; StateHasChanged(); return true;
    }
    private async Task EndAsync()
    {
        busy = false; pending = null; moving = null;
        if (disposed) return;
        try { if (owner is not null) await owner.InvokeVoidAsync("freeze", false); }
        catch (Exception exception) { await ErrorAsync(exception, "The catalogue interaction could not be completed. Your current input is retained."); }
        finally { if (modal is null) Shell.ReleaseRequirements(this); StateHasChanged(); }
        if (freshQueued && !BlockedRead) await RequestFreshnessAsync(false);
    }
    private Task SubmitAsync()
    {
        if (modal == "edit" && DraftValid)
        {
            object input = active switch
            {
                0 => PredicateDraft,
                1 => new ApplyRequirementCrest(draft!.Id, draft.Name, draft.Category, draft.Aliases, draft.Output, draft.Notes),
                2 => new ApplyRequirementMethod(draft!.Id, draft.Name, draft.Category, draft.Aliases, draft.Output, draft.Notes),
                3 => LiteralDraft,
                4 => new ApplyRequirementCountable(draft!.Id, draft.Name, draft.Category, draft.Aliases, draft.Output, draft.Notes),
                _ => new ApplyRequirementSoul(draft!.Id, draft.Name, draft.Category, draft.Aliases, draft.Output, draft.Notes)
            };
            return CommandAsync("apply", Kinds[active] + "/apply", input);
        }
        if (modal == "delete" && deleteRow is not null) return CommandAsync("delete", Kinds[active] + "/delete", new RequirementIdentity(deleteRow.Id));
        if (modal == "import" && importText is not null) return CommandAsync("replace", "import/replace", new RequirementImportText(importText));
        return Task.CompletedTask;
    }
    private async Task CommandAsync(string action, string path, object input)
    {
        if (!Shell.CanAuthor) return;
        if (!Begin(action)) return;
        using var timing = OperationTiming.Start(action == "replace" ? "requirements.import.replace" : "requirements.command");
        using var outcome = Runtime.ObserveMutation();
        var committed = false;
        string? historyWarning = null;
        try
        {
            if (owner is not null) await owner.InvokeVoidAsync("freeze", true);
            using var admissionTiming = OperationTiming.Start("requirements.command.admission");
            await Catalogue.MutateAsync(async admission =>
            {
            admissionTiming.End();
            if (disposed) return;
            // Immutable local response ownership, never a server save precondition
            // or shared-token comparison (definitions have no row timestamps).
            var source = admission.CaptureReadContext();
            var received = await PostAsync(path, input, outcome);
            var result = received.Result;
            try
            {
            if (disposed) return;
            if (result.Status != "committed") { timing.End(TimingOutcome.Rejected); Reject(result); return; }
            committed = true;
            historyWarning = result.HistoryWarning;
            // This input was accepted even if its carried facts cannot be installed.
            // Retire it now and close the native surface on every publication path.
            ClearModal();
            provisional = null;
            try
            {
                // Install carried commit facts through the sole confirmed owner.
                // A mismatched global token never turns this into a follow-up GET.
                if (result.Shared is null) throw new JsonException("The committed catalogue patch is unavailable.");
                admission.Publish(result.Shared, source);
                if (!disposed && Catalogue.Manager is { } fresh) Install(fresh);
            }
            finally { if (owner is not null) await owner.InvokeVoidAsync("close"); }
            timing.End(disposed ? TimingOutcome.Retired : TimingOutcome.Complete);
            }
            finally { Browser.Publish(received); }
            }, lifetime.Token);
        }
        catch (Exception exception)
        {
            timing.End(committed ? TimingOutcome.KnownCommitFollowupFailed : TimingOutcome.Uncertain);
            if (!disposed) await ErrorAsync(exception, committed
                ? "The change committed, but its catalogue result could not be incorporated. Refresh the catalogue to see current data."
                : "The change outcome could not be confirmed. Your current input is retained. Review current data before submitting again.");
        }
        finally
        {
            provisional = null;
            try { await EndAsync(); }
            finally { if (!disposed) await Shell.ReportHistoryWarningAsync(historyWarning); }
        }
    }
    private void Reject(RequirementOperationResult result)
    {
        provisional = null;
        if (modal is not null) { modalFeedback = result.Message; remoteIssues = result.Issues; }
        else feedback = result.Message ?? string.Join("\n", result.Issues.Select(IssueText));
    }
    private void StartDrag(Guid id)
    {
        if (!CanReorder || !Shell.LockRequirements(this)) return;
        generation++; dragged = id;
    }
    private void DragOver(Guid? id) { if (CanReorder && dragged is not null) { dropTarget = id; dropTail = id is null; } }
    private string? DropClass(Guid? id) => dragged == id && id is not null ? "catalogue-dragging" : CanReorder && dragged is not null && (id is null ? dropTail : dropTarget == id) ? "catalogue-drop-before" : null;
    private void ClearDrag()
    {
        var hadGesture = dragged is not null;
        dragged = null; dropTarget = null; dropTail = false;
        if (hadGesture && !busy && modal is null) Shell.ReleaseRequirements(this);
    }
    private async Task DropAsync(Guid? targetId)
    {
        var id = dragged;
        if (!CanReorder || id is null || id == targetId) { ClearDrag(); return; }
        var confirmed = lists![active];
        var source = confirmed.FindIndex(x => x.Id == id); var target = targetId is null ? confirmed.Count : confirmed.FindIndex(x => x.Id == targetId);
        if (source < 0 || target < 0) { ClearDrag(); return; }
        // Every row is a BEFORE target; the tail is after the last row. Convert
        // the insertion boundary into the existing final post-removal index.
        if (source < target) target--;
        if (source == target) { ClearDrag(); return; }
        var next = confirmed.ToList(); var row = next[source]; next.RemoveAt(source); next.Insert(target, row);
        provisional = next; moving = id;
        await CommandAsync("reorder", Kinds[active] + "/reorder", new RequirementOrder(id.Value, target));
    }
    private async Task ChooseImportAsync(InputFileChangeEventArgs args)
    {
        if (ActionsBlocked || !Begin("import")) return;
        using var timing = OperationTiming.Start("requirements.import.acquire-validate");
        try
        {
            var file = args.File;
            if (file.Size > 4 * 1024 * 1024) { timing.End(TimingOutcome.Rejected); feedback = "Catalogue imports must not exceed 4 MiB."; return; }
            await using var stream = file.OpenReadStream(4 * 1024 * 1024, lifetime.Token);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, lifetime.Token);
            // Preserve the strict UTF-8 document, including rejection of invalid
            // bytes/BOM/other encodings; never transcode or replace bad input.
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            var received = await PostAsync("import/validate", new RequirementImportText(text));
            var result = received.Result;
            try
            {
            if (disposed) return;
            if (result.Status != "committed") { timing.End(TimingOutcome.Rejected); Reject(result); return; }
            importText = text; modal = "import"; remoteIssues = []; modalFeedback = null;
            await PrepareDialogAsync();
            timing.End(disposed ? TimingOutcome.Retired : TimingOutcome.ReviewRequired);
            }
            finally { Browser.Publish(received); }
        }
        catch (Exception exception) { if (!disposed) await ErrorAsync(exception, "The catalogue import could not be prepared. No replacement was requested."); }
        finally { pickerGeneration++; await EndAsync(); }
    }
    private async Task ExportAsync()
    {
        if (ActionsBlocked || !Begin("export")) return;
        try
        {
            if (owner is null) throw new InvalidOperationException();
            var denial = await owner.InvokeAsync<AccessDenial?>("download", Access.Session?.WorkspaceKey);
            if (!disposed && denial is not null)
            {
                throw new SessionAccessException(denial, System.Net.HttpStatusCode.Forbidden);
            }
        }
        catch (Exception exception) { if (!disposed) await ErrorAsync(exception, "The saved catalogue could not be downloaded."); }
        finally { await EndAsync(); }
    }
    private async Task RecheckAsync()
    {
        if (ActionsBlocked || !Begin("recheck")) return;
        using var timing = OperationTiming.Start("requirements.recheck");
        using var outcome = Runtime.ObserveMutation();
        var committed = false;
        string? historyWarning = null;
        try
        {
            RequirementOperationResult? result = null;
            using var admissionTiming = OperationTiming.Start("requirements.recheck.admission");
            await Catalogue.MutateAsync(async admission =>
            {
                admissionTiming.End();
                var source = admission.CaptureReadContext();
                var received = await PostAsync("recheck", new { }, outcome);
                result = received.Result;
                try
                {
                if (result.Status != "committed") return;
                committed = true;
                historyWarning = result.HistoryWarning;
                if (disposed) return;
                if (result.Counts is not { Successful: >= 0, Failed: >= 0, Unknown: >= 0 }) throw new JsonException("Missing recheck counts.");
                counts = result.Counts;
                if (result.Shared is null) throw new JsonException("The saved recheck projection is unavailable. Use explicit Refresh to read current room data.");
                admission.Publish(result.Shared, source);
                }
                finally { Browser.Publish(received); }
            }, lifetime.Token);
            if (disposed) return;
            if (result is null) throw new JsonException();
            if (result.Status != "committed") { timing.End(TimingOutcome.Rejected); Reject(result); return; }
            timing.End();
        }
        catch (Exception exception) { timing.End(committed ? TimingOutcome.KnownCommitFollowupFailed : TimingOutcome.Uncertain); if (!disposed) await ErrorAsync(exception, committed ? "The requirement recheck committed, but its updated room facts could not be applied. Use Refresh; do not repeat the recheck as recovery." : "The requirement recheck outcome could not be confirmed. Review current data before running it again."); }
        finally
        {
            try { await EndAsync(); }
            finally { if (!disposed) await Shell.ReportHistoryWarningAsync(historyWarning); }
        }
    }
    private Task ErrorAsync(Exception exception, string message)
    { Console.Error.WriteLine(exception); return Shell.ReportRequirementsErrorAsync(exception, message); }

    public async ValueTask DisposeAsync()
    {
        Shell.ReleaseClientUpdateRisk(this);
        disposed = true; generation++; mutationEpoch++; ClearDrag(); provisional = null; guide = null;
        Catalogue.Changed -= CatalogueChanged;
        Shell.ReleaseRequirements(this);
        await lifetime.CancelAsync();
        try { if (owner is not null) { await owner.InvokeVoidAsync("dispose"); await owner.DisposeAsync(); } }
        catch (JSException exception) { Console.Error.WriteLine(exception); }
        callback?.Dispose();
        if (module is not null) await module.DisposeAsync();
        lifetime.Dispose();
    }
}
