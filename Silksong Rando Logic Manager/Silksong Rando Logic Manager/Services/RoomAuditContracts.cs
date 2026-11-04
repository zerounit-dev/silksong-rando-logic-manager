using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record AuditRoomCatalogueEntry(Guid Id, string FriendlyName, string? InGameId,
    Guid? GroupId, string? GroupName, int GroupSortOrder, int SortOrder);
public sealed record AuditRoomCatalogue(ImmutableArray<AuditRoomCatalogueEntry> Rooms);

// Audit evidence is transient: neither these trees nor scenario results are persisted.
public abstract record AuditExpression;
public sealed record AuditBoolean(bool IsAnd, AuditExpression Left, AuditExpression Right) : AuditExpression;
public sealed record AuditOperand(string Role, Guid? Id, string Value, string Source);
public sealed record AuditAtom(Guid PredicateId, string Signature, RequirementAtomFormatArguments Arguments,
    IReadOnlyList<AuditOperand> Operands, Guid? DependencyId, int Start, int Length) : AuditExpression;
public sealed record AuditRequirement(string Source, AuditExpression? Expression, IReadOnlyList<string> Reasons);
public sealed record AuditValue(Guid Id, string Name, string OutputValue);
// Retain every loaded definition, including unsupported input shapes. Recognition
// admission remains the translator's responsibility; preparation does not validate
// unused definitions or reinterpret their saved templates.
public sealed record AuditPredicateDefinition(Guid Id, string Name, string InputSyntax,
    string OutputSyntax, RequirementInputSyntax? InputShape);
public sealed record AuditSubroom(Guid Id, string Name);
public sealed record AuditRow(Guid Id, string Kind, string Name, Guid? SourceId, Guid? DestinationId,
    AuditRequirement Requirement, string? Alias = null, string? LocationType = null);
public sealed record AuditCheckIdentity(Guid Id, Guid RoomId, string Name, string RoomName);
public sealed record AuditTarget(string Kind, Guid Id);
public enum AuditEndpointMode { Neutral, Forward, Reverse, Routes }
// Reverse statuses are not forward Reached/Rows/proofs or an inventory. Hypothetical
// forward evaluations stay local to the evaluator operation, never actual Origins.
public sealed record AuditReverseAccess(AuditTarget Goal, IReadOnlyDictionary<AuditTarget, string> Statuses)
{
    public string Status(AuditTarget candidate) => Statuses.TryGetValue(candidate, out var status)
        ? status : "Unresolved subroom assignment";
}
public sealed record AuditSceneIntent(long Owner, string Kind, Guid Id, bool Origin);
public sealed record AuditRouteHighlight(IReadOnlyList<Guid> Connections, Guid? Step, IReadOnlyList<Guid>? Nodes = null, string? OriginKey = null);
// Invocation scope, never retained as scenario inputs or unioned into a proof.
public sealed record AuditOriginScope(string StartKey, bool AnyEntrance)
{
    public static AuditOriginScope For(string startKey, bool inspecting) => new(startKey, startKey.Length == 0 && inspecting);
    public bool Neutral => StartKey.Length == 0 && !AnyEntrance;
}
public sealed record AuditCurrentAccess(AuditOriginScope Scope, AuditEvaluation Unstarted,
    IReadOnlyDictionary<string, AuditEvaluation> Origins)
{
    public AuditEvaluation ForOrigin(string? key) => key is not null && Origins.TryGetValue(key, out var value) ? value : Unstarted;
    public bool NoEntrance => Scope.AnyEntrance && Origins.Count == 0;
}
// A transient comparison view over inputs and ONE origin's derived collection.
// Local identity comes from the evaluator's existing typed controls, not key spelling.
public sealed record AuditFactContext(AuditScenario Inputs, IReadOnlyDictionary<string, AuditControl> Controls,
    IReadOnlySet<Guid> Collected, AuditRequirementsContext Requirements)
{
    public int Level(string key) => Requirements.Advanced ? Inputs.Level(key) : 0;
    public bool IsLocal(string key) => Controls.TryGetValue(key, out var control) && control.LocalCheckId is not null;
    public bool Has(string key) => Inputs.Has(key) ||
        Controls.TryGetValue(key, out var control) && control.LocalCheckId is { } id && Collected.Contains(id);
    public bool Activated(Guid id) => Collected.Contains(id) || Controls.Values.Any(x => x.LocalCheckId == id && Inputs.Has(x.Key));
}
public sealed record AuditOriginComparison(string OriginKey, string Label, AuditEvaluation Evaluation);
public sealed record RoomAuditSnapshot(Guid RoomId, string Name, IReadOnlyList<AuditSubroom> Subrooms,
    IReadOnlyList<AuditRow> Rows, IReadOnlyList<AuditCheckIdentity> Checks, IReadOnlyList<AuditValue> Items,
    IReadOnlyList<AuditValue> Crests, IReadOnlyList<AuditValue> Methods, IReadOnlyList<AuditValue> Souls,
    IReadOnlyList<AuditPredicateDefinition> Predicates, IReadOnlyList<AuditValue> Countables,
    SceneLayoutView? Scene);
