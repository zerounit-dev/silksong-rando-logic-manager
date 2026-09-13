using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public static class DraftPersistenceService
{
    public static bool HasPersistableValues(Subroom draft) =>
        draft.ReferenceId != string.Empty ||
        draft.FriendlyName != string.Empty ||
        !string.IsNullOrEmpty(draft.Notes);

    public static bool HasPersistableValues(RoomTransition draft) =>
        draft.Alias != string.Empty ||
        draft.FriendlyName != string.Empty ||
        !string.IsNullOrEmpty(draft.SourceSubroomReferenceText) ||
        !string.IsNullOrEmpty(draft.DestinationRoomReferenceText) ||
        !string.IsNullOrEmpty(draft.DestinationTransitionAliasText) ||
        draft.Requirements != string.Empty ||
        draft.Notes != string.Empty ||
        draft.IsTodo ||
        draft.IsVerified is not null;

    public static bool HasPersistableValues(SubroomConnection draft) =>
        draft.Alias != string.Empty ||
        draft.FriendlyName != string.Empty ||
        draft.SourceSubroomReferenceText != string.Empty ||
        draft.DestinationSubroomReferenceText != string.Empty ||
        draft.Requirements != string.Empty ||
        draft.Notes != string.Empty ||
        draft.IsTodo ||
        draft.IsVerified is not null;

    public static bool HasPersistableValues(CheckLocation draft) =>
        draft.FriendlyName != string.Empty ||
        !string.IsNullOrEmpty(draft.SubroomReferenceText) ||
        draft.Requirements != string.Empty ||
        draft.Notes != string.Empty ||
        draft.LocationType is not null ||
        draft.IsTodo ||
        draft.IsVerified is not null;
}
