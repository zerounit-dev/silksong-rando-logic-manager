namespace Silksong_Rando_Logic_Manager.Services;

internal enum PreparedReferenceKind { Node, Port }
internal enum PreparedReferenceProblem { Blank, Missing, Ambiguous, Stale, Incomplete, InvalidIdentity, NonReciprocal }
internal sealed record PreparedGraphReference(PreparedReferenceKind Kind, PreparedReferenceProblem Problem,
    string Room, string RecordKind, string Record, Guid RecordId, string Field, string? Requested, string Cause,
    Guid RoomRecordId, RoomGraphExportFailure? HardFailure = null, IReadOnlyList<Guid>? RelatedRooms = null);

internal enum PreparedDependencyProblem { MissingOperand, InvalidScope, MissingRoom, AmbiguousRoom, MissingCheck, AmbiguousCheck, InvalidIdentity }
internal sealed record PreparedDependencyFailure(PreparedDependencyProblem Problem, string Operand, string Cause, Guid? RelatedRoom = null);
internal sealed record PreparedUnreadableReason(string Cause, string? CheckOperand = null, Guid? RelatedRoom = null);
internal sealed class PreparedRuleEvidence
{
    internal List<PreparedDependencyFailure> MissingChecks { get; } = [];
    internal List<PreparedUnreadableReason> UnreadableReasons { get; } = [];
    internal HashSet<string> ResolvedIds { get; } = new(StringComparer.Ordinal);
    internal string? UninspectedSyntax { get; set; }
}
internal sealed record PreparedGraphRule(string Room, string Kind, string Record, Guid RecordId, PreparedRuleEvidence Evidence, Guid RoomRecordId);
internal sealed record PreparedGraphRoom(RoomGraphRoom Room, bool? IsPlayable, IReadOnlySet<string> CoverageNodes,
    IReadOnlyList<Guid> LocationRecordIds, Guid RoomRecordId);
internal sealed record PreparedGraphRoomGroup(Guid Id, string Name);
internal sealed record PreparedGraphRoomIdentity(string Name, string Label, PreparedGraphRoomGroup? Group);

// This is operation-local evidence, not a serializable partial graph. The canonical
// document is available for traversal only when the ordinary export succeeded.
internal sealed class PreparedGraphEvidence
{
    internal List<PreparedGraphReference> References { get; } = [];
    internal List<PreparedGraphRule> Rules { get; } = [];
    internal IReadOnlyList<PreparedGraphRoom> Rooms { get; set; } = [];
    internal IReadOnlyDictionary<(string Kind, Guid Id), string> RecordLabels { get; set; } =
        new Dictionary<(string, Guid), string>();
    internal IReadOnlyDictionary<(string Kind, Guid Id), string> RecordTitles { get; set; } =
        new Dictionary<(string, Guid), string>();
    internal IReadOnlyDictionary<Guid, PreparedGraphRoomIdentity> RoomIdentities { get; set; } =
        new Dictionary<Guid, PreparedGraphRoomIdentity>();
    internal RoomGraphDocument? Document { get; set; }
    internal bool TranslationAvailable { get; set; }
}
internal sealed record PreparedRoomGraph(RoomGraphExportResult Export, PreparedGraphEvidence Evidence);
