using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

// Audit evidence is transient: neither these trees nor scenario results are persisted.
public abstract record AuditExpression;
public sealed record AuditBoolean(bool IsAnd, AuditExpression Left, AuditExpression Right) : AuditExpression;
public sealed record AuditOperand(string Role, Guid? Id, string Value, string Source);
public sealed record AuditAtom(Guid PredicateId, string Signature, RequirementAtomFormatArguments Arguments,
    IReadOnlyList<AuditOperand> Operands, Guid? DependencyId, int Start, int Length) : AuditExpression;
public sealed record AuditRequirement(string Source, AuditExpression? Expression, IReadOnlyList<string> Reasons);
public sealed record AuditValue(Guid Id, string Name, string OutputValue);
public sealed record AuditSubroom(Guid Id, string Name);
public sealed record AuditRow(Guid Id, string Kind, string Name, Guid? SourceId, Guid? DestinationId,
    AuditRequirement Requirement, string? Alias = null);
public sealed record AuditCheckIdentity(Guid Id, Guid RoomId, string Name, string RoomName);
public sealed record AuditTarget(string Kind, Guid Id);
public sealed record AuditSceneIntent(long Owner, string Kind, Guid Id, bool PickOrigin);
public sealed record AuditRouteHighlight(IReadOnlyList<Guid> Connections, Guid? Step);
public sealed record RoomAuditSnapshot(Guid RoomId, string Name, IReadOnlyList<AuditSubroom> Subrooms,
    IReadOnlyList<AuditRow> Rows, IReadOnlyList<AuditCheckIdentity> Checks, IReadOnlyList<AuditValue> Items,
    IReadOnlyList<AuditValue> Crests, IReadOnlyList<AuditValue> Methods, IReadOnlyList<AuditValue> Souls,
    SceneLayoutView? Scene);
