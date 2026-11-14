using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

// Visit-owned resource evidence. All access uses the existing visit gate, including
// upload registration, preview, operation registration and retirement.
internal sealed class RoomEditingImageState
{
    internal sealed class Upload(long? declaredLength)
    {
        public long? DeclaredLength { get; } = declaredLength;
        public Wire.RoomImageUploadStatus Status { get; set; } = Wire.RoomImageUploadStatus.Receiving;
        public RoomImageUpload? Resource { get; set; }
        public bool ReleasePending { get; set; }
    }
    internal sealed class Draft(Guid id, Wire.RoomImageOpenRequest open, SceneImageCaptureDraft capture, Wire.RoomImageDraftView view)
    {
        public Guid Id { get; } = id;
        public Wire.RoomImageOpenRequest Open { get; } = open;
        public SceneImageCaptureDraft Capture { get; set; } = capture;
        public Wire.RoomImageDraftView View { get; set; } = view;
        public long Revision { get; set; }
        public bool ReleasePending { get; set; }
        public Dictionary<Guid, Upload> Uploads { get; } = [];
        public Dictionary<Guid, (object Request, Wire.RoomImageDraftView Result)> Reads { get; } = [];
        public Dictionary<Guid, Wire.RoomImageReleaseRequest> Releases { get; } = [];
        public HashSet<Guid> Operations { get; } = [];
    }

    public Dictionary<Guid, Draft> Drafts { get; } = [];

    public Draft Get(Guid id) => Drafts.TryGetValue(id, out var draft) ? draft
        : throw new RoomTrackingProtocolException("The image draft does not belong to this visit.");

    public void Validate(Wire.RoomEditOperation operation)
    {
        (Guid DraftId, long Revision, Guid? UploadAttemptId)? data = operation switch
        {
            Wire.ApplyRoomSceneImage x => (x.DraftOwnerId, x.Revision, x.OwnedUploadAttemptId),
            Wire.ClearRoomSceneBounds x => (x.DraftOwnerId, x.Revision, (Guid?)null),
            _ => null
        };
        if (data is not { } values) return;
        var draft = Get(values.Item1);
        var baseline = operation switch
        {
            Wire.ApplyRoomSceneImage x => x.Baseline,
            Wire.ClearRoomSceneBounds x => x.Baseline,
            _ => null
        };
        if (baseline != draft.View.Baseline)
            throw new RoomTrackingProtocolException("The image baseline does not match the owned draft read.");
        if (draft.ReleasePending || values.Item2 < draft.Revision)
            throw new RoomTrackingProtocolException("The image draft revision is retired or obsolete.");
        if (values.Item3 is { } attempt && !draft.Uploads.ContainsKey(attempt))
            throw new RoomTrackingProtocolException("The image input does not belong to this draft.");
    }

    public void Pin(Wire.RoomQueuedOperation operation)
    {
        var id = operation.Operation switch
        {
            Wire.ApplyRoomSceneImage x => x.DraftOwnerId,
            Wire.ClearRoomSceneBounds x => x.DraftOwnerId,
            _ => Guid.Empty
        };
        if (id != Guid.Empty) Get(id).Operations.Add(operation.OperationId);
    }

    public void Complete(Guid operationId)
    {
        foreach (var draft in Drafts.Values.Where(x => x.Operations.Remove(operationId)))
        {
            draft.ReleasePending = true;
            foreach (var upload in draft.Uploads.Values) upload.ReleasePending = true;
        }
    }

    public IEnumerable<Guid> Retire()
    {
        foreach (var draft in Drafts.Values)
        {
            draft.ReleasePending = true;
            draft.Operations.Clear();
            foreach (var upload in draft.Uploads.Values)
            {
                upload.ReleasePending = true;
                if (upload.Resource is { } resource) yield return resource.Handle;
            }
        }
    }
}
