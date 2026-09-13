using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public enum ValidationSeverity
{
    Neutral,
    Warning,
    Danger
}

public enum RoomTransitionInverseState
{
    NoState,
    ZeroInverses,
    OneInverse,
    MultipleInverses
}

public sealed class LogicValidationService
{
    public ValidationSeverity RequiredNow(string? value) => HasText(value) ? ValidationSeverity.Neutral : ValidationSeverity.Warning;

    public ValidationSeverity Optional(string? value) => ValidationSeverity.Neutral;

    public ValidationSeverity RoomReferenceId(Room room, IEnumerable<Room> rooms) =>
        Duplicate(room, rooms, x => x.ReferenceId) ? ValidationSeverity.Danger : RequiredNow(room.ReferenceId);

    public ValidationSeverity RoomInGameId(Room room, IEnumerable<Room> rooms) =>
        HasText(room.InGameId) && Duplicate(room, rooms, x => x.InGameId) ? ValidationSeverity.Danger : ValidationSeverity.Neutral;

    public ValidationSeverity RoomSceneDimensions(Room room) =>
        ValidOptionalPositivePair(room.SceneUnitWidth, room.SceneUnitHeight) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity SubroomReferenceId(Subroom subroom, IEnumerable<Subroom> subrooms) =>
        Duplicate(subroom, subrooms, x => x.ReferenceId, x => x.RoomId == subroom.RoomId) ? ValidationSeverity.Danger : RequiredNow(subroom.ReferenceId);

    public ValidationSeverity SubroomFriendlyName(Subroom subroom, IEnumerable<Subroom> subrooms) =>
        Duplicate(subroom, subrooms, x => x.FriendlyName, x => x.RoomId == subroom.RoomId) ? ValidationSeverity.Danger : RequiredNow(subroom.FriendlyName);

    public ValidationSeverity SubroomSceneGeometry(Subroom subroom) =>
        ValidOptionalRectangle(subroom.SceneUnitX, subroom.SceneUnitY, subroom.SceneUnitWidth, subroom.SceneUnitHeight) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity TransitionAlias(RoomTransition transition, IEnumerable<RoomTransition> transitions) =>
        !HasText(transition.Alias) ? ValidationSeverity.Warning :
        !HasValidAlias(transition.Alias) || Duplicate(transition, transitions, x => x.Alias, x => x.RoomId == transition.RoomId) ? ValidationSeverity.Danger :
        ValidationSeverity.Neutral;

    public ValidationSeverity TransitionFriendlyName(RoomTransition transition, IEnumerable<RoomTransition> transitions) =>
        Duplicate(transition, transitions, x => x.FriendlyName, x => x.RoomId == transition.RoomId) ? ValidationSeverity.Danger : RequiredNow(transition.FriendlyName);

    public ValidationSeverity TransitionInGameId(RoomTransition transition, IEnumerable<RoomTransition> transitions, IEnumerable<CheckLocation> checks) =>
        !IsActive(transition) ? ValidationSeverity.Neutral :
        DuplicateImportedGameId(transition.InGameId, transitions.Where(x => IsActive(x) && !SameRecord(x, transition)).Select(x => x.InGameId).Concat(checks.Where(IsActive).Select(x => x.InGameId)));

    public ValidationSeverity TransitionAnnotationPosition(RoomTransition transition) =>
        ValidOptionalFinitePair(transition.AnnotationSceneUnitX, transition.AnnotationSceneUnitY) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity TransitionDestinationRoom(RoomTransition transition) => RequiredNow(transition.DestinationRoomReferenceText);

    public ValidationSeverity TransitionDestinationAlias(RoomTransition transition) =>
        !HasText(transition.DestinationTransitionAliasText) ? ValidationSeverity.Warning :
        HasValidAlias(transition.DestinationTransitionAliasText) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity TransitionDestinationPair(RoomTransition transition, IEnumerable<RoomTransition> transitions)
    {
        if (!HasCompleteDestination(transition))
        {
            return ValidationSeverity.Neutral;
        }

        return transitions.Count(candidate =>
            IsActive(candidate) &&
            candidate.RoomId == transition.RoomId &&
            HasCompleteDestination(candidate) &&
            EndpointMatches(candidate.DestinationRoomReferenceText, candidate.ResolvedDestinationRoomId, transition.DestinationRoomReferenceText, transition.ResolvedDestinationRoomId) &&
            EndpointMatches(candidate.DestinationTransitionAliasText, candidate.ResolvedDestinationTransitionId, transition.DestinationTransitionAliasText, transition.ResolvedDestinationTransitionId)) > 1
            ? ValidationSeverity.Danger
            : ValidationSeverity.Neutral;
    }

    public RoomTransitionInverseState TransitionInverseState(RoomTransition transition, IEnumerable<RoomTransition> transitions)
    {
        if (transition.ResolvedDestinationRoomId is not { } destinationRoomId ||
            transition.ResolvedDestinationTransitionId is not { })
        {
            return RoomTransitionInverseState.NoState;
        }

        var inverseCount = transitions.Count(candidate =>
            IsActive(candidate) &&
            candidate.RoomId == destinationRoomId &&
            candidate.ResolvedDestinationRoomId == transition.RoomId &&
            candidate.ResolvedDestinationTransitionId == transition.Id);

        return inverseCount switch
        {
            0 => RoomTransitionInverseState.ZeroInverses,
            1 => RoomTransitionInverseState.OneInverse,
            _ => RoomTransitionInverseState.MultipleInverses
        };
    }

    public ValidationSeverity CheckFriendlyName(CheckLocation check, IEnumerable<CheckLocation> checks)
    {
        if (!HasText(check.FriendlyName))
        {
            return ValidationSeverity.Warning;
        }

        var matches = checks.Where(candidate => IsActive(candidate) && TextMatches(candidate.FriendlyName, check.FriendlyName)).ToList();
        return matches.Count(candidate => candidate.RoomId == check.RoomId) > 1 ? ValidationSeverity.Danger :
            matches.Any(candidate => candidate.RoomId != check.RoomId) ? ValidationSeverity.Warning :
            ValidationSeverity.Neutral;
    }

    public ValidationSeverity CheckInGameId(CheckLocation check, IEnumerable<RoomTransition> transitions, IEnumerable<CheckLocation> checks) =>
        !IsActive(check) ? ValidationSeverity.Neutral :
        DuplicateImportedGameId(check.InGameId, transitions.Where(IsActive).Select(x => x.InGameId).Concat(checks.Where(x => IsActive(x) && !SameRecord(x, check)).Select(x => x.InGameId)));

    public ValidationSeverity CheckAnnotationPosition(CheckLocation check) =>
        ValidOptionalFinitePair(check.AnnotationSceneUnitX, check.AnnotationSceneUnitY) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity TransitionSourceSubroom(RoomTransition transition, IEnumerable<Subroom> subrooms) =>
        SubroomReferencePresence(transition.SourceSubroomReferenceText, HasActiveSubrooms(transition.RoomId, subrooms));

    public ValidationSeverity ConnectionSourceSubroom(SubroomConnection connection, IEnumerable<Subroom> subrooms) =>
        SubroomReferencePresence(connection.SourceSubroomReferenceText, HasActiveSubrooms(connection.RoomId, subrooms));

    public ValidationSeverity ConnectionDestinationSubroom(SubroomConnection connection, IEnumerable<Subroom> subrooms) =>
        SubroomReferencePresence(connection.DestinationSubroomReferenceText, HasActiveSubrooms(connection.RoomId, subrooms));

    public ValidationSeverity CheckSubroom(CheckLocation check, IEnumerable<Subroom> subrooms) =>
        SubroomReferencePresence(check.SubroomReferenceText, HasActiveSubrooms(check.RoomId, subrooms));

    public ValidationSeverity ConnectionAlias(SubroomConnection connection) =>
        !HasText(connection.Alias) ? ValidationSeverity.Warning :
        HasValidAlias(connection.Alias) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity ConnectionFriendlyName(SubroomConnection connection) => RequiredNow(connection.FriendlyName);

    public ValidationSeverity ConnectionSceneAnnotation(SubroomConnection connection) =>
        ValidOptionalFinitePair(connection.SceneUnitX, connection.SceneUnitY) ? ValidationSeverity.Neutral : ValidationSeverity.Danger;

    public ValidationSeverity ConnectionWithoutSubrooms(SubroomConnection connection, IEnumerable<Subroom> subrooms) =>
        IsActive(connection) && !HasActiveSubrooms(connection.RoomId, subrooms) ? ValidationSeverity.Danger : ValidationSeverity.Neutral;

    public ValidationSeverity ConnectionPathway(SubroomConnection connection, IEnumerable<SubroomConnection> connections)
    {
        if (!IsActive(connection) || !HasText(connection.Alias) || !HasText(connection.FriendlyName))
        {
            return ValidationSeverity.Neutral;
        }

        var roomConnections = connections.Where(IsActive).Where(x => x.RoomId == connection.RoomId).ToList();
        var pathway = roomConnections.Where(x => TextMatches(x.Alias, connection.Alias)).ToList();
        var aliasesForName = roomConnections.Where(x => HasText(x.FriendlyName) && TextMatches(x.FriendlyName, connection.FriendlyName)).ToList();

        return pathway.Count > 2 ||
            pathway.Any(x => !TextMatches(x.FriendlyName, connection.FriendlyName)) ||
            pathway.Any(x => !SameEndpointPair(x, connection)) ||
            pathway.Any(x => x.Id != connection.Id && SameDirection(x, connection)) ||
            aliasesForName.Any(x => !TextMatches(x.Alias, connection.Alias))
            ? ValidationSeverity.Danger
            : ValidationSeverity.Neutral;
    }

    public string ConnectionState(SubroomConnection connection, IEnumerable<SubroomConnection> connections)
    {
        if (!HasEndpoint(connection.SourceSubroomReferenceText, connection.ResolvedSourceSubroomId) ||
            !HasEndpoint(connection.DestinationSubroomReferenceText, connection.ResolvedDestinationSubroomId))
        {
            return "unresolved";
        }

        var reverseMatches = connections.Count(candidate =>
            candidate.Id != connection.Id &&
            IsActive(candidate) &&
            TextMatches(candidate.Alias, connection.Alias) &&
            EndpointMatches(candidate.SourceSubroomReferenceText, candidate.ResolvedSourceSubroomId, connection.DestinationSubroomReferenceText, connection.ResolvedDestinationSubroomId) &&
            EndpointMatches(candidate.DestinationSubroomReferenceText, candidate.ResolvedDestinationSubroomId, connection.SourceSubroomReferenceText, connection.ResolvedSourceSubroomId));

        return reverseMatches switch
        {
            0 => "one-way",
            1 => "bidirectional",
            _ => "ambiguous reverse match"
        };
    }

    public bool CanScaffoldInverse(SubroomConnection connection, IEnumerable<SubroomConnection> connections) =>
        IsActive(connection) &&
        ConnectionAlias(connection) == ValidationSeverity.Neutral &&
        ConnectionFriendlyName(connection) == ValidationSeverity.Neutral &&
        ConnectionPathway(connection, connections) == ValidationSeverity.Neutral &&
        connection.ResolvedSourceSubroomId is { } sourceId &&
        connection.ResolvedDestinationSubroomId is { } destinationId &&
        sourceId != destinationId &&
        ConnectionState(connection, connections) == "one-way";

    private static ValidationSeverity SubroomReferencePresence(string? referenceText, bool hasActiveSubrooms) =>
        hasActiveSubrooms == HasText(referenceText) ? ValidationSeverity.Neutral : ValidationSeverity.Warning;

    private static bool HasActiveSubrooms(Guid roomId, IEnumerable<Subroom> subrooms) => subrooms.Any(x => IsActive(x) && x.RoomId == roomId);

    private static bool HasCompleteDestination(RoomTransition transition) =>
        HasText(transition.DestinationRoomReferenceText) && HasText(transition.DestinationTransitionAliasText);

    private static ValidationSeverity DuplicateImportedGameId(string? gameId, IEnumerable<string?> otherGameIds) =>
        HasText(gameId) && otherGameIds.Any(other => TextMatches(other, gameId)) ? ValidationSeverity.Danger : ValidationSeverity.Neutral;

    private static bool HasValidAlias(string? alias) => alias is { Length: >= 1 and <= 3 };

    private static bool ValidOptionalPositivePair(double? first, double? second) =>
        first is null && second is null || first is { } firstValue && second is { } secondValue && double.IsFinite(firstValue) && double.IsFinite(secondValue) && firstValue > 0 && secondValue > 0;

    private static bool ValidOptionalFinitePair(double? first, double? second) =>
        first is null && second is null || first is { } firstValue && second is { } secondValue && double.IsFinite(firstValue) && double.IsFinite(secondValue);

    private static bool ValidOptionalRectangle(double? x, double? y, double? width, double? height) =>
        x is null && y is null && width is null && height is null ||
        x is { } xValue && y is { } yValue && width is { } widthValue && height is { } heightValue &&
        double.IsFinite(xValue) && double.IsFinite(yValue) && double.IsFinite(widthValue) && double.IsFinite(heightValue) && widthValue > 0 && heightValue > 0;

    private static bool HasEndpoint(string? text, Guid? id) => id is not null || HasText(text);

    private static bool SameEndpointPair(SubroomConnection left, SubroomConnection right) =>
        SameDirection(left, right) ||
        EndpointMatches(left.SourceSubroomReferenceText, left.ResolvedSourceSubroomId, right.DestinationSubroomReferenceText, right.ResolvedDestinationSubroomId) &&
        EndpointMatches(left.DestinationSubroomReferenceText, left.ResolvedDestinationSubroomId, right.SourceSubroomReferenceText, right.ResolvedSourceSubroomId);

    private static bool SameDirection(SubroomConnection left, SubroomConnection right) =>
        EndpointMatches(left.SourceSubroomReferenceText, left.ResolvedSourceSubroomId, right.SourceSubroomReferenceText, right.ResolvedSourceSubroomId) &&
        EndpointMatches(left.DestinationSubroomReferenceText, left.ResolvedDestinationSubroomId, right.DestinationSubroomReferenceText, right.ResolvedDestinationSubroomId);

    private static bool Duplicate<T>(T entity, IEnumerable<T> entities, Func<T, string?> text, Func<T, bool>? scope = null) where T : ArchivableEntity =>
        HasText(text(entity)) && entities.Count(candidate => IsActive(candidate) && (scope?.Invoke(candidate) ?? true) && TextMatches(text(candidate), text(entity))) > 1;

    private static bool IsActive(ArchivableEntity entity) => !entity.IsArchived;

    private static bool SameRecord<T>(T left, T right) where T : ArchivableEntity =>
        ReferenceEquals(left, right) || (right.Id != Guid.Empty && left.Id == right.Id);

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool TextMatches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool EndpointMatches(string? leftText, Guid? leftId, string? rightText, Guid? rightId) =>
        leftId is { } left && rightId is { } right ? left == right : TextMatches(leftText, rightText);
}
