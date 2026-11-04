using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal enum AuditNameRole { Predicate, Crest, Method, Item, Countable, Soul, Check }
internal sealed record AuditNameRef(AuditNameRole Role, Guid Id, string Name, Guid? RoomId = null, string? RoomName = null);
internal enum AuditDirection { Omitted, Run, Left, Right, Up, Down, Both, Either, Any, All }
internal enum AuditDirectionSummary { None, Single, Both, Either, Any, All, Conjunction, Alternatives }
internal enum AuditPresentationForm { Ordinary, LeftRight, Directions, Crest, Method, Technique, Soul }
internal enum AuditPartRole { Identity, Direction, Rank, IntrinsicHelper }
internal sealed record AuditPresentationQualifiers(string? Difficulty, int? Rank, AuditDirection Direction, int? Quantity);
// Local paths are within the bound application's original ordered tree, never
// occurrence IDs. Explanation instantiation qualifies them by the source occurrence.
internal sealed record AuditBoundSelection(ImmutableArray<int> ChoicePath, int Child);
internal sealed record AuditPartPlan(ImmutableArray<AuditNameRef> Names, AuditPartRole Role,
    AuditDirection Direction, AuditPrerequisiteEvidence Own, ImmutableArray<int> OwnPath,
    AuditConstraint? Represents = null);
internal sealed record AuditPresentationComponent(AuditPresentationForm Form, ImmutableArray<AuditNameRef> Heading,
    AuditPresentationQualifiers Qualifiers, ImmutableArray<AuditPartPlan> Parts, bool Joinable);
internal sealed record AuditVisibleVariant(int Refinement, ImmutableArray<AuditBoundSelection> Selections,
    ImmutableArray<AuditPresentationComponent> Components, AuditDirectionSummary DirectionSummary, AuditDirection SelectedDirection = AuditDirection.Omitted);
internal enum AuditPresentationDisposition { Body, HeaderOnly, None, Blocked }
internal sealed record AuditApplicationPresentation(AuditPresentationDisposition Disposition, ImmutableArray<AuditVisibleVariant> Variants,
    bool ExplicitAnyGeneratedDown);
internal sealed record AuditBoundApplication(AuditApplicationHandle Application, AuditPrerequisiteEvidence Prerequisite,
    AuditApplicationPresentation Presentation, ImmutableArray<string> Issues,
    string? ControlKey = null, bool Manual = false, Guid? LocalCheckId = null, string Identity = "");

internal sealed class PreparedAuditBindings(AuditPrerequisiteArena prerequisites,
    ImmutableArray<AuditBoundApplication> applications, ImmutableArray<AuditControl> controls)
{
    internal AuditPrerequisiteArena Prerequisites { get; } = prerequisites;
    internal ImmutableArray<AuditBoundApplication> Applications { get; } = applications;
    internal ImmutableArray<AuditControl> Controls { get; } = controls;
    internal FrozenDictionary<string, AuditControl> ControlByKey { get; } = controls.ToFrozenDictionary(x => x.Key, StringComparer.Ordinal);
}
