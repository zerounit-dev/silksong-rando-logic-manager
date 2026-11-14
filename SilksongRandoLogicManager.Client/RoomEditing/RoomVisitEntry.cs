using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.RoomEditing;

/// <summary>Immutable mounted entry evidence. A warm entry has no tracker until acknowledged.</summary>
public sealed class RoomVisitEntry
{
    public RoomTrackingOwner Owner { get; }
    public Guid RoomId => Document.Observation.RoomId;
    public RoomCompleteDocument Document { get; }
    public RoomBaselineAdmissionRequest? AdmissionRequest { get; }
    public RoomCompleteOpenResponse? CompleteOpen { get; }

    public RoomVisitEntry(RoomCompleteOpenResponse opened)
    {
        RoomDocumentReconciliation.Validate(opened.Document);
        Owner = new(opened.Identity.TabId, opened.Identity.VisitId);
        Document = opened.Document;
        CompleteOpen = opened;
    }

    public RoomVisitEntry(RoomTrackingOwner owner, RoomCompleteDocument localBaseline)
    {
        if (!RoomEditingIdentity.ValidOwner(owner)) throw new InvalidOperationException("Invalid entry owner.");
        RoomDocumentReconciliation.Validate(localBaseline);
        Owner = owner;
        Document = localBaseline;
        AdmissionRequest = new(owner, RoomEntryProtocol.Baseline(localBaseline));
    }
}
