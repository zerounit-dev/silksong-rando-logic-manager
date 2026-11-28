namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

public sealed record RequirementsParserRoomView(Guid RoomId, string ReferenceId);
public sealed record RequirementsParserCheckView(Guid RoomId, string FriendlyName);
/// <summary>Browser recognition input only; management/output metadata never crosses this boundary.</summary>
public sealed record RequirementsParserPredicateView(string InputSyntax, IReadOnlyList<string> Aliases);
/// <summary>Browser recognition input only; item output metadata remains formatter-owned.</summary>
public sealed record RequirementsParserItemView(IReadOnlyList<string> Aliases);
public sealed record RequirementsParserContextView(IReadOnlyList<RequirementsParserRoomView> Rooms,
    IReadOnlyList<RequirementsParserCheckView> Checks,
    IReadOnlyList<RequirementsParserPredicateView> Predicates,
    IReadOnlyList<RequirementsParserItemView> Items,
    IReadOnlyList<RequirementsParserCountableView> Countables,
    IReadOnlyList<RequirementsParserSoulView> Souls,
    IReadOnlyList<RequirementsParserCrestView> Crests,
    IReadOnlyList<RequirementsParserMethodView> Methods,
    Guid ExplanationRoomId, IReadOnlyList<string> ExplanationReferences);

public sealed record RequirementsParserCountableView(IReadOnlyList<string> Aliases);
public sealed record RequirementsParserSoulView(IReadOnlyList<string> Aliases);
public sealed record RequirementsParserCrestView(IReadOnlyList<string> Aliases);
public sealed record RequirementsParserMethodView(IReadOnlyList<string> Aliases);
