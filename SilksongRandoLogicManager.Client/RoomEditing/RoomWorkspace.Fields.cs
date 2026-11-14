using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.RoomEditing;

public sealed partial class RoomWorkspace
{
    private readonly record struct FieldKey(string Kind, string Client, string Field);
    private static FieldKey Key(RoomNativeField field) => new(field.Kind, field.Client, field.Field);
    private static string Boolean(bool value) => value ? "true" : "false";
    private static string Verification(bool? value) => value is { } flag ? Boolean(flag) : "";
    private static bool? ParseVerification(string value) => value switch { "true" => true, "false" => false, "" => null, _ => throw new InvalidOperationException("Invalid verification control value.") };
    private static string Kind(RoomTableRow row) => row switch { RoomSubroomRow => "subroom", RoomTransitionRow => "transition", RoomConnectionRow => "connection", RoomCheckRow => "check", _ => throw new InvalidOperationException() };
    private static Dictionary<string, string> Fields(RoomHeaderValues value) => new()
    {
        ["name"] = value.FriendlyName, ["reference"] = value.ReferenceId, ["game"] = value.InGameId ?? "",
        ["contributors"] = value.Contributors ?? "", ["comments"] = value.Comments ?? ""
    };
    private static Dictionary<string, string> Fields(RoomTableRow row) => row switch
    {
        RoomSubroomRow r => new() { ["name"] = r.Values.FriendlyName, ["reference"] = r.Values.ReferenceId, ["notes"] = r.Values.Notes ?? "" },
        RoomTransitionRow r => new()
        {
            ["alias"] = r.Values.Alias, ["name"] = r.Values.FriendlyName, ["source"] = r.Values.SourceSubroomReferenceText ?? "",
            ["destination-room"] = r.Values.DestinationRoomReferenceText ?? "", ["destination-alias"] = r.Values.DestinationTransitionAliasText ?? "",
            ["requirements"] = r.Values.Requirements, ["notes"] = r.Values.Notes, ["todo"] = Boolean(r.Values.IsTodo),
            ["verification"] = Verification(r.Values.IsVerified), ["unsafe-arrival"] = Boolean(r.Values.IsUnsafeArrival)
        },
        RoomConnectionRow r => new()
        {
            ["alias"] = r.Values.Alias, ["name"] = r.Values.FriendlyName, ["source"] = r.Values.SourceSubroomReferenceText,
            ["destination"] = r.Values.DestinationSubroomReferenceText, ["requirements"] = r.Values.Requirements,
            ["notes"] = r.Values.Notes, ["todo"] = Boolean(r.Values.IsTodo), ["verification"] = Verification(r.Values.IsVerified)
        },
        RoomCheckRow r => new()
        {
            ["name"] = r.Values.FriendlyName, ["subroom"] = r.Values.SubroomReferenceText ?? "", ["requirements"] = r.Values.Requirements,
            ["notes"] = r.Values.Notes, ["type"] = r.Values.LocationType ?? "", ["todo"] = Boolean(r.Values.IsTodo), ["verification"] = Verification(r.Values.IsVerified)
        },
        _ => throw new InvalidOperationException()
    };
    // Only native values that differ are copied. In particular, untouched nullable
    // authored values are not rewritten to empty strings by a different field's blur.
    private static RoomHeaderValues ReadHeader(RoomHeaderValues value, Func<string, string, string> read) => value with
    {
        FriendlyName = read("name", value.FriendlyName), ReferenceId = read("reference", value.ReferenceId),
        InGameId = NullableText("game", value.InGameId, read), Contributors = NullableText("contributors", value.Contributors, read),
        Comments = NullableText("comments", value.Comments, read)
    };
    private static string? NullableText(string field, string? value, Func<string, string, string> read)
    {
        var native = read(field, value ?? "");
        return native == (value ?? "") ? value : native;
    }
    private static void ReadRow(RoomTableRow row, Func<string, string, string> read)
    {
        switch (row)
        {
            case RoomSubroomRow r:
                r.Values = r.Values with { FriendlyName = read("name", r.Values.FriendlyName), ReferenceId = read("reference", r.Values.ReferenceId), Notes = NullableText("notes", r.Values.Notes, read) }; break;
            case RoomTransitionRow r:
                r.Values = r.Values with
                {
                    Alias = read("alias", r.Values.Alias), FriendlyName = read("name", r.Values.FriendlyName),
                    SourceSubroomReferenceText = NullableText("source", r.Values.SourceSubroomReferenceText, read),
                    DestinationRoomReferenceText = NullableText("destination-room", r.Values.DestinationRoomReferenceText, read),
                    DestinationTransitionAliasText = NullableText("destination-alias", r.Values.DestinationTransitionAliasText, read),
                    Requirements = read("requirements", r.Values.Requirements), Notes = read("notes", r.Values.Notes),
                    IsTodo = bool.Parse(read("todo", Boolean(r.Values.IsTodo))), IsVerified = ParseVerification(read("verification", Verification(r.Values.IsVerified))),
                    IsUnsafeArrival = bool.Parse(read("unsafe-arrival", Boolean(r.Values.IsUnsafeArrival)))
                }; break;
            case RoomConnectionRow r:
                r.Values = r.Values with
                {
                    Alias = read("alias", r.Values.Alias), FriendlyName = read("name", r.Values.FriendlyName),
                    SourceSubroomReferenceText = read("source", r.Values.SourceSubroomReferenceText), DestinationSubroomReferenceText = read("destination", r.Values.DestinationSubroomReferenceText),
                    Requirements = read("requirements", r.Values.Requirements), Notes = read("notes", r.Values.Notes),
                    IsTodo = bool.Parse(read("todo", Boolean(r.Values.IsTodo))), IsVerified = ParseVerification(read("verification", Verification(r.Values.IsVerified)))
                }; break;
            case RoomCheckRow r:
                r.Values = r.Values with
                {
                    FriendlyName = read("name", r.Values.FriendlyName), SubroomReferenceText = NullableText("subroom", r.Values.SubroomReferenceText, read),
                    Requirements = read("requirements", r.Values.Requirements), Notes = read("notes", r.Values.Notes), LocationType = NullableText("type", r.Values.LocationType, read),
                    IsTodo = bool.Parse(read("todo", Boolean(r.Values.IsTodo))), IsVerified = ParseVerification(read("verification", Verification(r.Values.IsVerified)))
                }; break;
        }
    }
    private static bool Meaningful(RoomTableRow row) => row switch
    {
        RoomSubroomRow r => !string.IsNullOrWhiteSpace(r.Values.FriendlyName) || !string.IsNullOrWhiteSpace(r.Values.ReferenceId) || !string.IsNullOrWhiteSpace(r.Values.Notes),
        RoomTransitionRow r => new[] { r.Values.Alias, r.Values.FriendlyName, r.Values.SourceSubroomReferenceText, r.Values.DestinationRoomReferenceText, r.Values.DestinationTransitionAliasText, r.Values.Requirements, r.Values.Notes }.Any(x => !string.IsNullOrWhiteSpace(x)) || r.Values.IsTodo || r.Values.IsVerified is not null || r.Values.IsUnsafeArrival,
        RoomConnectionRow r => new[] { r.Values.Alias, r.Values.FriendlyName, r.Values.SourceSubroomReferenceText, r.Values.DestinationSubroomReferenceText, r.Values.Requirements, r.Values.Notes }.Any(x => !string.IsNullOrWhiteSpace(x)) || r.Values.IsTodo || r.Values.IsVerified is not null,
        RoomCheckRow r => new[] { r.Values.FriendlyName, r.Values.SubroomReferenceText, r.Values.Requirements, r.Values.Notes }.Any(x => !string.IsNullOrEmpty(x)) || r.Values.LocationType != (r.Enemies ? "enemy" : null) || r.Values.IsTodo || r.Values.IsVerified is not null,
        _ => false
    };
    private static RoomOrderSection Section(RoomTableRow row) => row switch
    {
        RoomSubroomRow => RoomOrderSection.Subrooms, RoomTransitionRow => RoomOrderSection.Transitions,
        RoomConnectionRow => RoomOrderSection.Connections, RoomCheckRow { Enemies: true } => RoomOrderSection.Enemies,
        RoomCheckRow => RoomOrderSection.Checks, _ => throw new InvalidOperationException()
    };
    private static RoomTableRow Empty(RoomOrderSection section, Guid id) => section switch
    {
        RoomOrderSection.Subrooms => new RoomSubroomRow(id, new("", "", null)),
        RoomOrderSection.Transitions => new RoomTransitionRow(id, new("", "", null, null, null, "", "", false, null, false)),
        RoomOrderSection.Connections => new RoomConnectionRow(id, new("", "", "", "", "", "", false, null)),
        _ => new RoomCheckRow(id, new("", null, "", "", section == RoomOrderSection.Enemies ? "enemy" : null, false, null), section == RoomOrderSection.Enemies)
    };
    private static RoomRowOrigin<T> Origin<T>(RoomTableRow row, T values) => new(
        row.Dependencies.IsEmpty ? RoomOriginKind.Observed : RoomOriginKind.Projected, values, row.LastConfirmed, row.Dependencies);
    private static RoomTableRow Copy(RoomTableRow row)
    {
        RoomTableRow copy = row switch
        {
            RoomSubroomRow r => new RoomSubroomRow(r.ClientId, r.Initial) { Values = r.Values, Geometry = r.Geometry },
            RoomTransitionRow r => new RoomTransitionRow(r.ClientId, r.Initial) { Values = r.Values, Metadata = r.Metadata, EnableAnnotation = r.EnableAnnotation,
                InverseIcon = r.InverseIcon, InverseClass = r.InverseClass, InverseTitle = r.InverseTitle, CanPrepareInverse = r.CanPrepareInverse, InverseRoomId = r.InverseRoomId, DestinationAliases = r.DestinationAliases },
            RoomConnectionRow r => new RoomConnectionRow(r.ClientId, r.Initial) { Values = r.Values, X = r.X, Y = r.Y, EnableAnnotation = r.EnableAnnotation,
                PathwayIcon = r.PathwayIcon, PathwayClass = r.PathwayClass, PathwayTitle = r.PathwayTitle, CanScaffoldInverse = r.CanScaffoldInverse,
                AnnotationMarkerId = r.AnnotationMarkerId, AttentionSourceSubroomId = r.AttentionSourceSubroomId, AttentionDestinationSubroomId = r.AttentionDestinationSubroomId },
            RoomCheckRow r => new RoomCheckRow(r.ClientId, r.Initial, r.Enemies) { Values = r.Values, Metadata = r.Metadata, EnableAnnotation = r.EnableAnnotation },
            _ => throw new InvalidOperationException()
        };
        copy.EntityId = row.EntityId; copy.LastConfirmed = row.LastConfirmed; copy.IsArchived = row.IsArchived;
        copy.Pending = row.Pending; copy.Attention = row.Attention; copy.Dependencies = row.Dependencies;
        copy.FieldClasses = row.FieldClasses; copy.FieldTitles = row.FieldTitles; copy.AnnotationClass = row.AnnotationClass;
        copy.AnnotationTitle = row.AnnotationTitle; copy.AnnotationAction = row.AnnotationAction; copy.CanAnnotate = row.CanAnnotate;
        return copy;
    }
}
