using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private sealed record PresentationInputs(RoomCompleteDocument Document, RoomDiagnosticIndex.Facts Global,
        IReadOnlyList<RoomSubroomRow> Subrooms, IReadOnlyList<RoomTransitionRow> Transitions,
        IReadOnlyList<RoomConnectionRow> Connections, RoomCheckSectionRows Checks, RoomCheckSectionRows Enemies);
    private PresentationInputs? preparedPresentation;

    private void EnsurePresentation()
    {
        if (workspace.Confirmed.Document is not { } document) return;
        var previous = preparedPresentation;
        // Stage mutates private new rows/copies, then Install replaces ALL lists.
        // Outside Stage, ReadRow only mutates private copies. Presentation is the
        // sole writer of derived flags. Native values do not mutate these inputs.
        // Thus these explicit document/global/list identities certify every input
        // consumed by Apply; observation or the Rooms dictionary alone cannot.
        if (previous is not null && ReferenceEquals(previous.Document, document)
            && ReferenceEquals(previous.Global, Catalogue.Diagnostics)
            && ReferenceEquals(previous.Subrooms, workspace.Subrooms)
            && ReferenceEquals(previous.Transitions, workspace.Transitions)
            && ReferenceEquals(previous.Connections, workspace.Connections)
            && ReferenceEquals(previous.Checks, workspace.CheckLocations)
            && ReferenceEquals(previous.Enemies, workspace.Enemies)) return;
        ObserveShellOperation();
        using var work = observedRoomWork.Measure(ObservedWork.Tables);
        RoomTableDiagnostics.Apply(document, Catalogue.Diagnostics, workspace.Subrooms, workspace.Transitions, workspace.Connections,
            workspace.CheckLocations.Active.Concat(workspace.CheckLocations.Archived).Concat(workspace.Enemies.Active).Concat(workspace.Enemies.Archived).ToArray());
        // Failed mapping never certifies default or partially prepared guidance.
        preparedPresentation = new(document, Catalogue.Diagnostics, workspace.Subrooms, workspace.Transitions,
            workspace.Connections, workspace.CheckLocations, workspace.Enemies);
    }
    private RoomTableRow CurrentPresentationRow(string kind, Guid id)
    {
        EnsurePresentation();
        return workspace.Find(kind, id);
    }
}
