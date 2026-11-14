using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record AuditSnapshotResponse([property: JsonRequired] RoomAuditSnapshot? Snapshot);
public sealed record AuditRoomCatalogueEntry(Guid Id, string FriendlyName, string? InGameId,
    Guid? GroupId, string? GroupName, int GroupSortOrder, int SortOrder);
public sealed record AuditRoomCatalogue(ImmutableArray<AuditRoomCatalogueEntry> Rooms);

// Closed transient recognition wire; never an engine handle or persisted expression.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(AuditBoolean), "boolean")]
[JsonDerivedType(typeof(AuditAtom), "atom")]
public abstract record AuditExpression;
public sealed record AuditBoolean(bool IsAnd, AuditExpression Left, AuditExpression Right) : AuditExpression;
public sealed record AuditOperand(string Role, Guid? Id, string Value, string Source);
public sealed record AuditAtom(Guid PredicateId, string Signature, RequirementAtomFormatArguments Arguments,
    IReadOnlyList<AuditOperand> Operands, Guid? DependencyId, int Start, int Length) : AuditExpression;
public sealed record AuditRequirement(string Source, AuditExpression? Expression, IReadOnlyList<string> Reasons);
public sealed record AuditValue(Guid Id, string Name, string OutputValue);
public sealed record AuditPredicateDefinition(Guid Id, string Name, string InputSyntax,
    string OutputSyntax, RequirementInputSyntax? InputShape);
public sealed record AuditSubroom(Guid Id, string Name);
public sealed record AuditRow(Guid Id, string Kind, string Name, Guid? SourceId, Guid? DestinationId,
    AuditRequirement Requirement, string? Alias = null, string? LocationType = null);
public sealed record AuditCheckIdentity(Guid Id, Guid RoomId, string Name, string RoomName);
public sealed record RoomAuditSnapshot(Guid RoomId, string Name, IReadOnlyList<AuditSubroom> Subrooms,
    IReadOnlyList<AuditRow> Rows, IReadOnlyList<AuditCheckIdentity> Checks, IReadOnlyList<AuditValue> Items,
    IReadOnlyList<AuditValue> Crests, IReadOnlyList<AuditValue> Methods, IReadOnlyList<AuditValue> Souls,
    IReadOnlyList<AuditPredicateDefinition> Predicates, IReadOnlyList<AuditValue> Countables,
    SceneLayoutView? Scene);
