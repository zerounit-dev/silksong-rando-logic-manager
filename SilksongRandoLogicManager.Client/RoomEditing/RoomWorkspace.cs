using System.Collections.Immutable;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.RoomEditing;

/// <summary>One browser visit's actual typed room workspace; no transport or EF state.</summary>
public sealed partial class RoomWorkspace
{
    private RoomEditQueue? queue;
    private readonly RoomConfirmedState entryPresentation;
    public RoomVisitEntry Entry { get; }
    public RoomOpenResponse? Admission { get; private set; }
    public bool IsAdmitted => queue is not null;
    public RoomEditQueue? AdmittedQueue => queue;
    public RoomEditQueue Queue => queue ?? throw new InvalidOperationException("Room tracking admission has not completed.");
    public RoomConfirmedState Confirmed => queue?.Confirmed ?? entryPresentation;
    public RoomHeaderValues Header { get; private set; }
    public RoomHeaderValues InitialHeader { get; }
    public IReadOnlyList<RoomSubroomRow> Subrooms { get; private set; } = [];
    public IReadOnlyList<RoomTransitionRow> Transitions { get; private set; } = [];
    public IReadOnlyList<RoomConnectionRow> Connections { get; private set; } = [];
    public RoomCheckSectionRows CheckLocations { get; private set; } = new([], []);
    public RoomCheckSectionRows Enemies { get; private set; } = new([], []);
    public IReadOnlyDictionary<RoomOrderSection, Guid> LatentSuccessors => latent.ToDictionary(x => x.Key, x =>
        revertedCreations.Values.Any(row => Section(row) == x.Key) && !rows.Any(row => row.EntityId is null && row.ClientId == tails[x.Key]) ? tails[x.Key] : x.Value);
    private readonly Dictionary<RoomOrderSection, Guid> latent = Enum.GetValues<RoomOrderSection>().ToDictionary(x => x, _ => Guid.NewGuid());
    private readonly Dictionary<RoomOrderSection, Guid> tails = Enum.GetValues<RoomOrderSection>().ToDictionary(x => x, _ => Guid.NewGuid());
    private Dictionary<FieldKey, long> accepted = [];
    private readonly Dictionary<Guid, RoomOrderSection> checkOperationSections = [];
    private readonly Dictionary<Guid, ImmutableArray<RoomNativeField>> creationFields = [];
    private Dictionary<Guid, RoomTableRow> revertedCreations = [];
    private List<RoomTableRow> rows = [];
    private ImmutableArray<RoomNativeField> live = [];
    public long InteractionGeneration { get; private set; }
    public string? BoundaryFeedback { get; private set; }
    public void SetBoundaryFeedback(string value) => BoundaryFeedback = value;
    public bool LastBoundaryAccepted { get; private set; } = true;
    public event Action? AcceptanceObserved;
    public bool AcceptanceFailed { get; private set; }
    public void ClearAcceptanceFailure() => AcceptanceFailed = false;
    private T Accept<T>(Func<T> accept)
    {
        var before = AdmittedQueue?.QueuedThrough ?? 0;
        try { return accept(); }
        catch { if ((AdmittedQueue?.QueuedThrough ?? 0) != before) AcceptanceFailed = true; throw; }
        finally { if ((AdmittedQueue?.QueuedThrough ?? 0) != before) AcceptanceObserved?.Invoke(); }
    }
    public bool AuthoringBlocked => !IsAdmitted || Queue.TrackingUnavailable || Queue.Blocked is not null || Queue.ProjectionPending || Queue.Confirmed.Document is null;
    public RoomWorkspace(RoomVisitEntry entry)
    {
        Entry = entry;
        entryPresentation = RoomDocumentReconciliation.FromComplete(entry.Document);
        InitialHeader = Header = entry.Document.Header.Values;
        Install(Stage(entryPresentation, [], [], null));
        if (entry.CompleteOpen is { } complete) AttachAdmission(complete);
    }
    public void AttachAdmission(RoomOpenResponse admission)
    {
        if (queue is not null) throw new InvalidOperationException("The room queue is already attached.");
        if (admission.Identity.TabId != Entry.Owner.TabId || admission.Identity.VisitId != Entry.Owner.VisitId
            || admission.Identity.RoomId != Entry.RoomId
            || (Entry.AdmissionRequest is { } request
                ? admission is not RoomBaselineAdmissionResponse warm || !RoomEntryProtocol.ValidBaseline(warm.Baseline, Entry.RoomId)
                    || !RoomEntryProtocol.SameBaseline(request.Baseline, warm.Baseline)
                : admission is not RoomCompleteOpenResponse cold || !ReferenceEquals(cold, Entry.CompleteOpen)))
            throw new InvalidOperationException("The admission does not belong to this entry baseline.");
        // Validate zero progress and the real identity BEFORE publishing attachment.
        var admitted = new RoomEditQueue(admission.Identity, admission.Progress, Entry.Document);
        Admission = admission;
        queue = admitted;
    }
    public void Observe(ImmutableArray<RoomNativeField> fields)
    {
        ValidateNative(fields);
        if (!live.SequenceEqual(fields)) InteractionGeneration++;
        live = fields;
    }
    public bool HasLocalWork(ImmutableArray<RoomNativeField> native)
    {
        var frame = IndexNative(native);
        if (!IsAdmitted) return true; // Pending tracking cannot certify replacement safety.
        if (native.Any(x => x.Composing) || Queue.HasPendingIntent || Queue.RequestOutstanding || Queue.RecoveryRequired || AuthoringBlocked) return true;
        // Edit generations protect installation; they are not dirty flags. Typing
        // and then undoing to the projected value must not permanently trap a visit.
        var header = native.Where(x => x.Kind == "header").ToDictionary(x => x.Field);
        if (ReadHeader(Header, (field, current) => header.TryGetValue(field, out var value) ? value.Value : current) != Header) return true;
        foreach (var row in rows)
        {
            var kind = Kind(row); var client = row.ClientId.ToString();
            var draft = Copy(row);
            ReadRow(draft, (field, current) => frame.TryGetValue(new(kind, client, field), out var value) ? value.Value : current);
            if (row.EntityId is null ? Meaningful(draft) : !Fields(row).SequenceEqual(Fields(draft))) return true;
        }
        return false;
    }
    public RoomQueuedOperation? AcceptHeader(ImmutableArray<RoomNativeField> native)
        => Accept(() => AcceptHeaderCore(native));
    private RoomQueuedOperation? AcceptHeaderCore(ImmutableArray<RoomNativeField> native)
    {
        if (AuthoringBlocked) return null;
        Observe(native);
        var map = native.Where(x => x.Kind == "header").ToDictionary(x => x.Field);
        var submitted = ReadHeader(Header, (field, current) => map.TryGetValue(field, out var value) ? value.Value : current);
        RoomQueuedOperation? item = null;
        if (submitted != Header)
        {
            var document = Queue.Confirmed.Document ?? throw new InvalidOperationException();
            var dependencies = Queue.PendingIntent.Where(x => x.Operation is SaveRoomHeader).Select(x => x.OperationId).ToImmutableArray();
            item = Queue.Enqueue(new SaveRoomHeader(new(dependencies.IsEmpty ? RoomOriginKind.Observed : RoomOriginKind.Projected,
                Header, document.Header.Observation, dependencies), submitted));
        }
        AcceptGenerations(native.Where(x => x.Kind == "header"));
        if (item is not null || !CanRetainProjection(native)) Install(Stage(Queue.Confirmed, Queue.PendingIntent, native, Queue.Blocked));
        return item;
    }
    public RoomQueuedOperation? AcceptRow(string kind, Guid clientId, ImmutableArray<RoomNativeField> native)
        => Accept(() => AcceptRowCore(kind, clientId, native));
    private RoomQueuedOperation? AcceptRowCore(string kind, Guid clientId, ImmutableArray<RoomNativeField> native)
    {
        LastBoundaryAccepted = true; BoundaryFeedback = null;
        if (AuthoringBlocked) return null;
        Observe(native);
        var original = rows.Single(x => Kind(x) == kind && x.ClientId == clientId);
        if (original.IsArchived) return null;
        var submitted = Copy(original);
        var map = native.Where(x => x.Kind == kind && x.Client == clientId.ToString()).ToDictionary(x => x.Field);
        ReadRow(submitted, (field, current) => map.TryGetValue(field, out var value) ? value.Value : current);
        if (original is RoomCheckRow { EntityId: null } && Meaningful(submitted))
        {
            var competing = Queue.PendingIntent.Any(x => x.Operation is CreateRoomCheck)
                || rows.OfType<RoomCheckRow>().Where(x => x.EntityId is null && x.ClientId != clientId).Any(other =>
                {
                    var draft = Copy(other);
                    var fields = native.Where(x => x.Kind == "check" && x.Client == other.ClientId.ToString()).ToDictionary(x => x.Field);
                    ReadRow(draft, (field, current) => fields.TryGetValue(field, out var value) ? value.Value : current);
                    return Meaningful(draft);
                });
            if (competing)
            {
                LastBoundaryAccepted = false;
                BoundaryFeedback = "Finish the pending check or enemy creation before creating another. Both drafts have been retained.";
                return null;
            }
        }
        RoomEditOperation? operation = (original, submitted) switch
        {
            (RoomSubroomRow a, RoomSubroomRow b) when a.EntityId is null && Meaningful(b) => new CreateRoomSubroom(a.ClientId, b.Values),
            (RoomTransitionRow a, RoomTransitionRow b) when a.EntityId is null && Meaningful(b) => new CreateRoomTransition(a.ClientId, b.Values),
            (RoomConnectionRow a, RoomConnectionRow b) when a.EntityId is null && Meaningful(b) => new CreateRoomConnection(a.ClientId, b.Values),
            (RoomCheckRow a, RoomCheckRow b) when a.EntityId is null && Meaningful(b) => new CreateRoomCheck(a.ClientId, b.Values),
            (RoomSubroomRow a, RoomSubroomRow b) when a.EntityId is { } id && a.Values != b.Values => new SaveRoomSubroom(id, Origin(a, a.Values), b.Values),
            (RoomTransitionRow a, RoomTransitionRow b) when a.EntityId is { } id && a.Values != b.Values => new SaveRoomTransition(id, Origin(a, a.Values), b.Values),
            (RoomConnectionRow a, RoomConnectionRow b) when a.EntityId is { } id && a.Values != b.Values => new SaveRoomConnection(id, Origin(a, a.Values), b.Values),
            (RoomCheckRow a, RoomCheckRow b) when a.EntityId is { } id && a.Values != b.Values => new SaveRoomCheck(id, Origin(a, a.Values), b.Values),
            _ => null
        };
        var item = operation is null ? null : Queue.Enqueue(operation);
        if (item is not null && original is RoomCheckRow) checkOperationSections.Add(item.OperationId, Section(original));
        if (original.EntityId is null && operation is not null)
        {
            creationFields.Add(item!.OperationId, map.Values.ToImmutableArray());
            var section = Section(original);
            if (!revertedCreations.Remove(original.ClientId)) { tails[section] = latent[section]; latent[section] = Guid.NewGuid(); }
        }
        AcceptGenerations(map.Values);
        if (item is not null || !CanRetainProjection(native)) Install(Stage(Queue.Confirmed, Queue.PendingIntent, native, Queue.Blocked));
        return item;
    }
    public RoomQueuedOperation Enqueue(RoomEditOperation operation)
        => Accept(() => EnqueueCore(operation));
    private RoomQueuedOperation EnqueueCore(RoomEditOperation operation)
    {
        if (AuthoringBlocked) throw new InvalidOperationException("Room authoring is not admitted.");
        var item = Queue.Enqueue(operation);
        var check = rows.OfType<RoomCheckRow>().FirstOrDefault(x => x.EntityId is { } id && OperationRows(operation).Contains(id));
        if (check is not null) checkOperationSections.Add(item.OperationId, Section(check));
        Install(Stage(Queue.Confirmed, Queue.PendingIntent, live, Queue.Blocked));
        return item;
    }
    public void AdvanceEmptyTail(string kind, Guid clientId)
    {
        var row = rows.SingleOrDefault(x => Kind(x) == kind && x.ClientId == clientId && x.EntityId is null && !x.IsArchived);
        if (row is null || AuthoringBlocked) return;
        var draft = Copy(row);
        var fields = live.Where(x => x.Kind == kind && x.Client == clientId.ToString()).ToDictionary(x => x.Field);
        ReadRow(draft, (field, current) => fields.TryGetValue(field, out var value) ? value.Value : current);
        if (Meaningful(draft)) return;
        var section = Section(row);
        if (!revertedCreations.Remove(clientId)) { tails[section] = latent[section]; latent[section] = Guid.NewGuid(); }
        Install(Stage(Queue.Confirmed, Queue.PendingIntent, live, Queue.Blocked));
    }
    public RoomOriginalPlacement Placement(RoomTableRow row)
    {
        var section = Section(row);
        var membership = rows.Where(x => Section(x) == section && !x.IsArchived && x.EntityId is not null).Select(x => x.EntityId!.Value).ToImmutableArray();
        return new(section, membership.IndexOf(row.EntityId ?? throw new InvalidOperationException()), membership);
    }
    public RoomTableRow Find(string kind, Guid id) => rows.Single(x => Kind(x) == kind && (x.ClientId == id || x.EntityId == id));

    /// <summary>
    /// Browser WASM's in-process reference is intentional: native read/write, final
    /// later-intent projection, workspace publication and I publication run without
    /// an await/render/event gap. Native generations are sampled at the actual write.
    /// </summary>
    public void Incorporate(RoomTrackingResponse response, IJSInProcessObjectReference nativeOwner,
        Func<IReadOnlyList<SharedMutationSegment>, PreparedCataloguePublication> preparePublication)
    {
        using var timing = OperationTiming.Start("save.response.prepare-native-publication-incorporation");
        // Validate owned receipt/affected evidence and prepare shared storage before
        // touching native values or I. The visit adopts original outcome evidence;
        // it is not a second current catalogue. Storage may preserve newer facts
        // without vetoing historical outcome accounting or demanding a fresh read.
        var prepared = Queue.PrepareIncorporation(response);
        var publish = preparePublication(response.Results.Where(x => x.Receipt.Sequence > Queue.IncorporatedThrough
                && x.Receipt.Sequence <= response.Progress.TerminalThrough && !x.ProjectionPending && x.Shared is not null)
            .OrderBy(x => x.Receipt.Sequence).Select(x => x.Shared!).ToArray());
        var snapshot = nativeOwner.Invoke<RoomNativeField[]>("snapshotRoomNative").ToImmutableArray();
        ValidateNative(snapshot);
        var restored = new Dictionary<Guid, RoomTableRow>(revertedCreations);
        var restoredAccepted = new Dictionary<FieldKey, long>(accepted);
        foreach (var result in response.Results.Where(x => x.Receipt.Sequence > Queue.IncorporatedThrough && x.Receipt.Sequence <= prepared.IncorporatedThrough
            && x.Receipt.Disposition == RoomTerminalDisposition.UserReverted))
        {
            var operation = Queue.PendingIntent.Single(x => x.OperationId == result.Receipt.OperationId);
            var draft = CreationDraft(operation.Operation);
            if (draft is null) continue;
            restored[draft.ClientId] = draft;
            if (creationFields.TryGetValue(operation.OperationId, out var frame))
                foreach (var field in frame) restoredAccepted[Key(field)] = field.Generation;
        }
        var first = Stage(prepared.Confirmed, prepared.PendingIntent, snapshot, prepared.Response.Blocked, restored);
        using var nativeTiming = OperationTiming.Start("save.native-install-restage");
        var patches = NativePatches(first, snapshot, restoredAccepted);
        var installed = nativeOwner.Invoke<RoomNativeField[]>("installRoomNative", new object?[] { patches, false }).ToImmutableArray();
        ValidateNative(installed);
        var final = Stage(prepared.Confirmed, prepared.PendingIntent, installed, prepared.Response.Blocked, restored);
        nativeTiming.End();
        publish.Install(() => Queue.CommitInstallation(prepared, () =>
        {
            live = installed; revertedCreations = restored;
            accepted = restoredAccepted;
            Install(final);
        }));
        timing.End(Queue.ProjectionPending ? TimingOutcome.KnownCommitFollowupFailed : Queue.Blocked is not null ? TimingOutcome.ReviewRequired : TimingOutcome.Incorporated);
    }
    public bool ReplaceExternal(RoomCompleteDocument? document, RoomConfirmedState expected, IJSInProcessObjectReference nativeOwner)
    {
        if (!ReferenceEquals(Queue.Confirmed, expected)) return false;
        var snapshot = nativeOwner.Invoke<RoomNativeField[]>("snapshotRoomNative").ToImmutableArray();
        if (HasLocalWork(snapshot)) return false;
        var confirmed = document is null ? new RoomConfirmedState(null, false) : RoomDocumentReconciliation.FromComplete(document);
        if (document is not null && document.Observation.RoomId != Queue.Identity.RoomId) throw new InvalidOperationException("Wrong external room document.");
        var staged = Stage(confirmed, [], snapshot, null);
        // The preflight above proved there are no live differences. These exact
        // observed generations may now receive authoritative merged values.
        var previous = new Dictionary<FieldKey, long>(accepted);
        try
        {
            AcceptGenerations(snapshot);
            var installed = nativeOwner.Invoke<RoomNativeField[]>("installRoomNative", new object?[] { NativePatches(staged, snapshot), true }).ToImmutableArray();
            ValidateNative(installed);
            if (installed.Any(x => x.Composing))
            {
                // Native installation refused the entire external patch atomically.
                // Keep the old document/generations and the caller's deferred signal.
                accepted = previous;
                return false;
            }
            var final = Stage(confirmed, [], installed, null);
            Queue.InstallExternal(confirmed, () => { live = installed; Install(final); });
            return true;
        }
        catch
        {
            accepted.Clear(); foreach (var pair in previous) accepted.Add(pair.Key, pair.Value);
            throw;
        }
    }
    private void AcceptGenerations(IEnumerable<RoomNativeField> fields)
    {
        foreach (var field in fields) accepted[Key(field)] = field.Generation;
    }
    // This is deliberately stronger than "no operation": incomplete tails, restored
    // creators, retained missing rows and queued projection work use the full path.
    // With no such structure, an exact typed native/projection match leaves Stage
    // with the same confirmed rows, ordinary tails and header it last installed.
    private bool CanRetainProjection(ImmutableArray<RoomNativeField> native)
    {
        if (Queue.HasPendingIntent || Queue.RequestOutstanding || Queue.RecoveryRequired || AuthoringBlocked
            || revertedCreations.Count != 0 || rows.Any(x => x.Attention || x.Pending)) return false;
        var frame = IndexNative(native);
        var header = native.Where(x => x.Kind == "header").ToDictionary(x => x.Field);
        if (ReadHeader(Header, (field, current) => header.TryGetValue(field, out var value) ? value.Value : current) != Header) return false;
        foreach (var row in rows)
        {
            var kind = Kind(row); var client = row.ClientId.ToString(); var draft = Copy(row);
            ReadRow(draft, (field, current) => frame.TryGetValue(new(kind, client, field), out var value) ? value.Value : current);
            if (!Fields(row).SequenceEqual(Fields(draft))) return false;
        }
        return true;
    }
    private static Dictionary<FieldKey, RoomNativeField> IndexNative(ImmutableArray<RoomNativeField> fields)
    {
        if (fields.IsDefault) throw new InvalidOperationException("Invalid native room field snapshot.");
        var index = new Dictionary<FieldKey, RoomNativeField>(fields.Length);
        foreach (var field in fields)
            if (field is null || field.Generation < 0 || field.Value is null || field.Field is null || field.Kind is null || field.Client is null
                || !index.TryAdd(Key(field), field)) throw new InvalidOperationException("Invalid native room field snapshot.");
        return index;
    }
    private static void ValidateNative(ImmutableArray<RoomNativeField> fields)
    {
        if (fields.IsDefault || fields.Any(x => x is null || x.Generation < 0 || x.Value is null || x.Field is null || x.Kind is null || x.Client is null)
            || fields.Select(Key).Distinct().Count() != fields.Length) throw new InvalidOperationException("Invalid native room field snapshot.");
    }
    private sealed record Installation(RoomHeaderValues Header, List<RoomTableRow> Rows,
        IReadOnlyList<RoomSubroomRow> Subrooms, IReadOnlyList<RoomTransitionRow> Transitions, IReadOnlyList<RoomConnectionRow> Connections,
        RoomCheckSectionRows CheckLocations, RoomCheckSectionRows Enemies);
    private static Installation Materialize(RoomHeaderValues header, List<RoomTableRow> rows) => new(header, rows,
        rows.OfType<RoomSubroomRow>().ToArray(), rows.OfType<RoomTransitionRow>().ToArray(), rows.OfType<RoomConnectionRow>().ToArray(),
        new(rows.OfType<RoomCheckRow>().Where(x => !x.Enemies && !x.IsArchived).ToArray(), rows.OfType<RoomCheckRow>().Where(x => !x.Enemies && x.IsArchived).ToArray()),
        new(rows.OfType<RoomCheckRow>().Where(x => x.Enemies && !x.IsArchived).ToArray(), rows.OfType<RoomCheckRow>().Where(x => x.Enemies && x.IsArchived).ToArray()));
    private void Install(Installation value)
    {
        Header = value.Header; rows = value.Rows; Subrooms = value.Subrooms; Transitions = value.Transitions; Connections = value.Connections;
        CheckLocations = value.CheckLocations; Enemies = value.Enemies;
    }
    private RoomNativeField[] NativePatches(Installation projection, ImmutableArray<RoomNativeField> snapshot, IReadOnlyDictionary<FieldKey, long>? generations = null)
    {
        var desired = new Dictionary<FieldKey, string>();
        foreach (var pair in Fields(projection.Header)) desired[new("header", "", pair.Key)] = pair.Value;
        foreach (var row in projection.Rows)
            foreach (var pair in Fields(row)) desired[new(Kind(row), row.ClientId.ToString(), pair.Key)] = pair.Value;
        var observed = snapshot.ToDictionary(Key);
        // Installation includes absent fields too: their retained native frame must
        // be current before I, not depend on a later archive/section/comments mount.
        return desired.Select(pair => observed.TryGetValue(pair.Key, out var field)
            ? field with { Value = field.Composing || field.Generation > (generations ?? accepted).GetValueOrDefault(pair.Key) ? field.Value : pair.Value }
            : new RoomNativeField(pair.Key.Kind, pair.Key.Client, pair.Key.Field, pair.Value, 0, false)).ToArray();
    }
    private RoomTableRow? CreationDraft(RoomEditOperation operation)
    {
        var id = operation switch
        {
            CreateRoomSubroom x => x.EntityId, CreateRoomTransition x => x.EntityId,
            CreateRoomConnection x => x.EntityId, CreateRoomCheck x => x.EntityId, _ => Guid.Empty
        };
        if (id == Guid.Empty || rows.SingleOrDefault(x => x.ClientId == id) is not { } previous) return null;
        var draft = Copy(previous);
        draft.EntityId = null; draft.LastConfirmed = null; draft.Pending = draft.Attention = false; draft.Dependencies = [];
        switch (operation)
        {
            case CreateRoomSubroom x: ((RoomSubroomRow)draft).Values = x.Submitted; break;
            case CreateRoomTransition x: ((RoomTransitionRow)draft).Values = x.Submitted; break;
            case CreateRoomConnection x: ((RoomConnectionRow)draft).Values = x.Submitted; break;
            case CreateRoomCheck x: ((RoomCheckRow)draft).Values = x.Submitted; break;
        }
        return draft;
    }
}
