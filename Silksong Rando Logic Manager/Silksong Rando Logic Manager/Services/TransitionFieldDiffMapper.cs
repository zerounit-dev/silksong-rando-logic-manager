using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Explicitly defines the transition fields an ordinary editor mutation may own.</summary>
public static class TransitionFieldDiffMapper
{
    public static readonly IReadOnlyList<string> EditableFields =
    [
        nameof(RoomTransition.Alias), nameof(RoomTransition.FriendlyName),
        nameof(RoomTransition.InGameId), nameof(RoomTransition.InGamePositionX),
        nameof(RoomTransition.InGamePositionY), nameof(RoomTransition.InGamePositionZ),
        nameof(RoomTransition.LocalPositionX), nameof(RoomTransition.LocalPositionY), nameof(RoomTransition.LocalPositionZ),
        nameof(RoomTransition.AnnotationSceneUnitX), nameof(RoomTransition.AnnotationSceneUnitY), nameof(RoomTransition.EnableAnnotation),
        nameof(RoomTransition.SourceSubroomReferenceText), nameof(RoomTransition.DestinationRoomReferenceText),
        nameof(RoomTransition.DestinationTransitionAliasText), nameof(RoomTransition.Requirements), nameof(RoomTransition.Notes),
        nameof(RoomTransition.IsTodo), nameof(RoomTransition.IsVerified)
    ];

    public static IReadOnlyList<string> Differences(RoomTransition baseline, RoomTransition draft) =>
        EditableFields.Where(field => !Equals(Value(baseline, field), Value(draft, field))).ToArray();

    public static void Apply(RoomTransition target, RoomTransition source, IEnumerable<string> fields)
    {
        foreach (var field in fields) Set(target, field, Value(source, field));
    }

    /// <summary>Applies only this operation's committed fields and resolver-owned state.</summary>
    public static void Reconcile(RoomTransition live, RoomTransition committed, RoomTransition request, IReadOnlyCollection<string> committedFields, bool resolved)
    {
        foreach (var field in committedFields)
        {
            // A value typed after the request snapshot remains owned by the later edit.
            if (Equals(Value(live, field), Value(request, field))) Set(live, field, Value(committed, field));
        }

        live.UpdatedUtc = committed.UpdatedUtc;
        live.RequirementsParseSucceeded = committed.RequirementsParseSucceeded;
        if (resolved)
        {
            live.ResolvedSourceSubroomId = committed.ResolvedSourceSubroomId;
            live.ResolvedDestinationRoomId = committed.ResolvedDestinationRoomId;
            live.ResolvedDestinationTransitionId = committed.ResolvedDestinationTransitionId;
        }
    }

    public static RoomTransition Clone(RoomTransition value) => new()
    {
        Id = value.Id, RoomId = value.RoomId, Alias = value.Alias, FriendlyName = value.FriendlyName,
        InGameId = value.InGameId, InGamePositionX = value.InGamePositionX, InGamePositionY = value.InGamePositionY,
        InGamePositionZ = value.InGamePositionZ, LocalPositionX = value.LocalPositionX, LocalPositionY = value.LocalPositionY,
        LocalPositionZ = value.LocalPositionZ, AnnotationSceneUnitX = value.AnnotationSceneUnitX, AnnotationSceneUnitY = value.AnnotationSceneUnitY, EnableAnnotation = value.EnableAnnotation,
        SourceSubroomReferenceText = value.SourceSubroomReferenceText, DestinationRoomReferenceText = value.DestinationRoomReferenceText,
        DestinationTransitionAliasText = value.DestinationTransitionAliasText, Requirements = value.Requirements,
        RequirementsParseSucceeded = value.RequirementsParseSucceeded, Notes = value.Notes,
        ResolvedSourceSubroomId = value.ResolvedSourceSubroomId, ResolvedDestinationRoomId = value.ResolvedDestinationRoomId,
        ResolvedDestinationTransitionId = value.ResolvedDestinationTransitionId, SortOrder = value.SortOrder, IsTodo = value.IsTodo,
        IsVerified = value.IsVerified, IsArchived = value.IsArchived, ArchivedUtc = value.ArchivedUtc,
        CreatedUtc = value.CreatedUtc, UpdatedUtc = value.UpdatedUtc
    };

    private static object? Value(RoomTransition row, string field) => field switch
    {
        nameof(RoomTransition.Alias) => row.Alias, nameof(RoomTransition.FriendlyName) => row.FriendlyName,
        nameof(RoomTransition.InGameId) => row.InGameId, nameof(RoomTransition.InGamePositionX) => row.InGamePositionX,
        nameof(RoomTransition.InGamePositionY) => row.InGamePositionY, nameof(RoomTransition.InGamePositionZ) => row.InGamePositionZ,
        nameof(RoomTransition.LocalPositionX) => row.LocalPositionX, nameof(RoomTransition.LocalPositionY) => row.LocalPositionY,
        nameof(RoomTransition.LocalPositionZ) => row.LocalPositionZ, nameof(RoomTransition.AnnotationSceneUnitX) => row.AnnotationSceneUnitX,
        nameof(RoomTransition.AnnotationSceneUnitY) => row.AnnotationSceneUnitY, nameof(RoomTransition.EnableAnnotation) => row.EnableAnnotation, nameof(RoomTransition.SourceSubroomReferenceText) => row.SourceSubroomReferenceText,
        nameof(RoomTransition.DestinationRoomReferenceText) => row.DestinationRoomReferenceText,
        nameof(RoomTransition.DestinationTransitionAliasText) => row.DestinationTransitionAliasText,
        nameof(RoomTransition.Requirements) => row.Requirements, nameof(RoomTransition.Notes) => row.Notes,
        nameof(RoomTransition.IsTodo) => row.IsTodo, nameof(RoomTransition.IsVerified) => row.IsVerified,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static void Set(RoomTransition row, string field, object? value)
    {
        switch (field)
        {
            case nameof(RoomTransition.Alias): row.Alias = (string)value!; break; case nameof(RoomTransition.FriendlyName): row.FriendlyName = (string)value!; break;
            case nameof(RoomTransition.InGameId): row.InGameId = (string?)value; break; case nameof(RoomTransition.InGamePositionX): row.InGamePositionX = (double?)value; break;
            case nameof(RoomTransition.InGamePositionY): row.InGamePositionY = (double?)value; break; case nameof(RoomTransition.InGamePositionZ): row.InGamePositionZ = (double?)value; break;
            case nameof(RoomTransition.LocalPositionX): row.LocalPositionX = (double?)value; break; case nameof(RoomTransition.LocalPositionY): row.LocalPositionY = (double?)value; break;
            case nameof(RoomTransition.LocalPositionZ): row.LocalPositionZ = (double?)value; break; case nameof(RoomTransition.AnnotationSceneUnitX): row.AnnotationSceneUnitX = (double?)value; break;
            case nameof(RoomTransition.AnnotationSceneUnitY): row.AnnotationSceneUnitY = (double?)value; break; case nameof(RoomTransition.EnableAnnotation): row.EnableAnnotation = (bool)value!; break;
            case nameof(RoomTransition.SourceSubroomReferenceText): row.SourceSubroomReferenceText = (string?)value; break; case nameof(RoomTransition.DestinationRoomReferenceText): row.DestinationRoomReferenceText = (string?)value; break;
            case nameof(RoomTransition.DestinationTransitionAliasText): row.DestinationTransitionAliasText = (string?)value; break;
            case nameof(RoomTransition.Requirements): row.Requirements = (string)value!; break; case nameof(RoomTransition.Notes): row.Notes = (string)value!; break;
            case nameof(RoomTransition.IsTodo): row.IsTodo = (bool)value!; break; case nameof(RoomTransition.IsVerified): row.IsVerified = (bool?)value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }
}

/// <summary>Typed adapter retaining the established transition mapper contract.</summary>
public sealed class TransitionChildFieldDiffMapper : IChildRowFieldDiffMapper<RoomTransition>
{
    public IReadOnlyList<string> EditableFields => TransitionFieldDiffMapper.EditableFields;
    public RoomTransition Clone(RoomTransition value) => TransitionFieldDiffMapper.Clone(value);
    public IReadOnlyList<string> Differences(RoomTransition baseline, RoomTransition draft) => TransitionFieldDiffMapper.Differences(baseline, draft);
    public void Apply(RoomTransition target, RoomTransition source, IEnumerable<string> fields) => TransitionFieldDiffMapper.Apply(target, source, fields);
    public void Reconcile(RoomTransition live, RoomTransition committed, RoomTransition request, IReadOnlyCollection<string> fields, bool resolved) => TransitionFieldDiffMapper.Reconcile(live, committed, request, fields, resolved);
}
