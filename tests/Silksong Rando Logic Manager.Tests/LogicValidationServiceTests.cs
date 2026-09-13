using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class LogicValidationServiceTests
{
    private readonly LogicValidationService validation = new();

    [Fact]
    public void ConnectionDraft_KeyboardFocusTraversalWithoutCurrentValueDoesNotPersist()
    {
        var draft = new SubroomConnection();

        Assert.False(DraftPersistenceService.HasPersistableValues(draft));
    }

    [Fact]
    public void ConnectionDraft_ClearingTheOnlyEditedValueDoesNotPersist()
    {
        var draft = new SubroomConnection { Alias = "a" };

        draft.Alias = string.Empty;

        Assert.False(DraftPersistenceService.HasPersistableValues(draft));
    }

    [Fact]
    public void ConnectionDraft_BlankAliasAndNamePersistWhenAnotherValueExists()
    {
        Assert.True(DraftPersistenceService.HasPersistableValues(new SubroomConnection { Requirements = "requires dash" }));
    }

    [Fact]
    public void AllDummyDrafts_DiscardCurrentDefaultsAndPersistCurrentNonDefaultValues()
    {
        Assert.False(DraftPersistenceService.HasPersistableValues(new Subroom()));
        Assert.False(DraftPersistenceService.HasPersistableValues(new RoomTransition()));
        Assert.False(DraftPersistenceService.HasPersistableValues(new SubroomConnection()));
        Assert.False(DraftPersistenceService.HasPersistableValues(new CheckLocation()));

        Assert.True(DraftPersistenceService.HasPersistableValues(new Subroom { Notes = "note" }));
        Assert.True(DraftPersistenceService.HasPersistableValues(new RoomTransition { IsVerified = false }));
        Assert.True(DraftPersistenceService.HasPersistableValues(new SubroomConnection { IsVerified = true }));
        Assert.True(DraftPersistenceService.HasPersistableValues(new CheckLocation { IsVerified = false }));
        Assert.True(DraftPersistenceService.HasPersistableValues(new SubroomConnection { IsTodo = true }));
        Assert.True(DraftPersistenceService.HasPersistableValues(new CheckLocation { IsIncludedInApworld = false }));
    }

    [Fact]
    public void RequiredAndOptionalAuthoringFields_UseWarningAndNeutralSeverities()
    {
        Assert.Equal(ValidationSeverity.Warning, validation.RequiredNow(" "));
        Assert.Equal(ValidationSeverity.Neutral, validation.RequiredNow("value"));
        Assert.Equal(ValidationSeverity.Neutral, validation.Optional(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Requirements_UseRequiredNowWarningForBlankAuthoredText(string? requirements)
    {
        Assert.Equal(ValidationSeverity.Warning, validation.RequiredNow(requirements));
        Assert.Equal(ValidationSeverity.Neutral, validation.RequiredNow("requires dash"));
    }

    [Fact]
    public void RoomIdentifiers_FlagActiveNormalizedDuplicatesButNotArchivedRows()
    {
        var room = new Room { ReferenceId = " Bone ", InGameId = "ID" };
        var duplicate = new Room { ReferenceId = "bone", InGameId = "id" };

        Assert.Equal(ValidationSeverity.Danger, validation.RoomReferenceId(room, [room, duplicate]));
        Assert.Equal(ValidationSeverity.Danger, validation.RoomInGameId(room, [room, duplicate]));
        duplicate.IsArchived = true;
        Assert.Equal(ValidationSeverity.Neutral, validation.RoomReferenceId(room, [room, duplicate]));
        Assert.Equal(ValidationSeverity.Neutral, validation.RoomInGameId(room, [room, duplicate]));
    }

    [Fact]
    public void SubroomNamesAndReferences_FlagDuplicatesOnlyWithinTheirRoom()
    {
        var roomId = Guid.NewGuid();
        var subroom = new Subroom { RoomId = roomId, FriendlyName = "Name", ReferenceId = "ref" };
        var duplicate = new Subroom { RoomId = roomId, FriendlyName = " name ", ReferenceId = "REF" };
        var elsewhere = new Subroom { RoomId = Guid.NewGuid(), FriendlyName = "Name", ReferenceId = "ref" };

        Assert.Equal(ValidationSeverity.Danger, validation.SubroomFriendlyName(subroom, [subroom, duplicate, elsewhere]));
        Assert.Equal(ValidationSeverity.Danger, validation.SubroomReferenceId(subroom, [subroom, duplicate, elsewhere]));
        Assert.Equal(ValidationSeverity.Neutral, validation.SubroomFriendlyName(subroom, [subroom, elsewhere]));
        Assert.Equal(ValidationSeverity.Neutral, validation.SubroomReferenceId(subroom, [subroom, elsewhere]));
    }

    [Fact]
    public void TransitionAliasesAndNames_FlagMissingInvalidAndDuplicateValues()
    {
        var roomId = Guid.NewGuid();
        var transition = new RoomTransition { RoomId = roomId, Alias = "A", FriendlyName = "Exit" };
        var duplicate = new RoomTransition { RoomId = roomId, Alias = " a ", FriendlyName = " exit " };

        Assert.Equal(ValidationSeverity.Warning, validation.TransitionAlias(new RoomTransition(), []));
        Assert.Equal(ValidationSeverity.Danger, validation.TransitionAlias(new RoomTransition { Alias = "long" }, []));
        Assert.Equal(ValidationSeverity.Danger, validation.TransitionAlias(transition, [transition, duplicate]));
        Assert.Equal(ValidationSeverity.Warning, validation.TransitionFriendlyName(new RoomTransition(), []));
        Assert.Equal(ValidationSeverity.Danger, validation.TransitionFriendlyName(transition, [transition, duplicate]));
    }

    [Fact]
    public void ImportedGameIds_FlagActiveNormalizedDuplicatesAcrossTransitionsAndChecks()
    {
        var transition = new RoomTransition { InGameId = " transition-id " };
        var matchingCheck = new CheckLocation { InGameId = "TRANSITION-ID" };
        var matchingTransition = new RoomTransition { InGameId = "transition-id" };
        matchingTransition.IsArchived = true;

        Assert.Equal(ValidationSeverity.Danger, validation.TransitionInGameId(transition, [transition], [matchingCheck]));
        Assert.Equal(ValidationSeverity.Danger, validation.CheckInGameId(matchingCheck, [transition], [matchingCheck]));

        matchingCheck.IsArchived = true;
        Assert.Equal(ValidationSeverity.Neutral, validation.TransitionInGameId(transition, [transition, matchingTransition], [matchingCheck]));
        Assert.Equal(ValidationSeverity.Neutral, validation.CheckInGameId(matchingCheck, [transition, matchingTransition], [matchingCheck]));
        Assert.Equal(ValidationSeverity.Neutral, validation.TransitionInGameId(new RoomTransition(), [], []));
    }

    [Fact]
    public void TransitionDestinations_FlagMissingInvalidAndCompleteDuplicatePairs()
    {
        var roomId = Guid.NewGuid();
        var destinationRoom = Guid.NewGuid();
        var destinationTransition = Guid.NewGuid();
        var transition = new RoomTransition { RoomId = roomId, DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "in", ResolvedDestinationRoomId = destinationRoom, ResolvedDestinationTransitionId = destinationTransition };
        var duplicate = new RoomTransition { RoomId = roomId, DestinationRoomReferenceText = "other text", DestinationTransitionAliasText = "other", ResolvedDestinationRoomId = destinationRoom, ResolvedDestinationTransitionId = destinationTransition };

        Assert.Equal(ValidationSeverity.Warning, validation.TransitionDestinationRoom(new RoomTransition()));
        Assert.Equal(ValidationSeverity.Warning, validation.TransitionDestinationAlias(new RoomTransition()));
        Assert.Equal(ValidationSeverity.Danger, validation.TransitionDestinationAlias(new RoomTransition { DestinationTransitionAliasText = "long" }));
        Assert.Equal(ValidationSeverity.Danger, validation.TransitionDestinationPair(transition, [transition, duplicate]));
        Assert.Equal(ValidationSeverity.Neutral, validation.TransitionDestinationPair(new RoomTransition { RoomId = roomId, DestinationRoomReferenceText = "target" }, [transition]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TransitionInverseState_ReturnsNoStateWhenEitherResolvedEndpointIsMissing(bool hasDestinationRoom, bool hasDestinationTransition)
    {
        var transition = new RoomTransition
        {
            ResolvedDestinationRoomId = hasDestinationRoom ? Guid.NewGuid() : null,
            ResolvedDestinationTransitionId = hasDestinationTransition ? Guid.NewGuid() : null
        };

        Assert.Equal(RoomTransitionInverseState.NoState, validation.TransitionInverseState(transition, []));
    }

    [Fact]
    public void TransitionInverseState_ReturnsZeroInversesWhenNoActiveResolvedReturnExists()
    {
        var source = TransitionWithResolvedDestination();

        Assert.Equal(RoomTransitionInverseState.ZeroInverses, validation.TransitionInverseState(source, [source]));
    }

    [Fact]
    public void TransitionInverseState_ReturnsOneInverseForOneActiveResolvedReturn()
    {
        var source = TransitionWithResolvedDestination();
        var inverse = ResolvedInverse(source);

        Assert.Equal(RoomTransitionInverseState.OneInverse, validation.TransitionInverseState(source, [source, inverse]));
    }

    [Fact]
    public void TransitionInverseState_ReturnsMultipleInversesForMultipleActiveResolvedReturns()
    {
        var source = TransitionWithResolvedDestination();

        Assert.Equal(
            RoomTransitionInverseState.MultipleInverses,
            validation.TransitionInverseState(source, [source, ResolvedInverse(source), ResolvedInverse(source)]));
    }

    [Fact]
    public void TransitionInverseState_DoesNotUseAuthoredTextWhenResolvedIdsAreMissing()
    {
        var source = TransitionWithResolvedDestination();
        var authoredMatch = new RoomTransition
        {
            RoomId = source.ResolvedDestinationRoomId!.Value,
            DestinationRoomReferenceText = "source room",
            DestinationTransitionAliasText = "source exit"
        };

        Assert.Equal(RoomTransitionInverseState.ZeroInverses, validation.TransitionInverseState(source, [source, authoredMatch]));
    }

    [Fact]
    public void TransitionInverseSetup_ProjectsOnlyBlankFieldsAndRequiresAUniqueReverseSourceEndpoint()
    {
        var sourceRoom = new Room { Id = Guid.NewGuid(), ReferenceId = " source " };
        var targetRoom = new Room { Id = Guid.NewGuid(), ReferenceId = "target" };
        var source = new RoomTransition { Id = Guid.NewGuid(), RoomId = sourceRoom.Id, Alias = "OUT", ResolvedDestinationRoomId = targetRoom.Id };
        var target = new RoomTransition { Id = Guid.NewGuid(), RoomId = targetRoom.Id, FriendlyName = "Target exit" };
        source.ResolvedDestinationTransitionId = target.Id;

        var blank = TransitionInverseSetupService.CreateDraft(source, [sourceRoom, targetRoom], [source, target]);
        Assert.NotNull(blank);
        Assert.True(blank.FillDestinationRoomReference);
        Assert.True(blank.FillDestinationTransitionAlias);

        target.DestinationRoomReferenceText = "conflicting room";
        var partial = TransitionInverseSetupService.CreateDraft(source, [sourceRoom, targetRoom], [source, target]);
        Assert.NotNull(partial);
        Assert.False(partial.FillDestinationRoomReference);
        Assert.True(partial.FillDestinationTransitionAlias);

        target.DestinationTransitionAliasText = "conflicting alias";
        Assert.Null(TransitionInverseSetupService.CreateDraft(source, [sourceRoom, targetRoom], [source, target]));

        target.DestinationTransitionAliasText = null;
        var duplicateAlias = new RoomTransition { Id = Guid.NewGuid(), RoomId = sourceRoom.Id, Alias = " out " };
        Assert.Null(TransitionInverseSetupService.CreateDraft(source, [sourceRoom, targetRoom], [source, target, duplicateAlias]));

        source.Alias = "";
        Assert.Null(TransitionInverseSetupService.CreateDraft(source, [sourceRoom, targetRoom], [source, target]));
    }

    [Fact]
    public void TransitionInverseSetupQueue_PreservesImportedPromptOrder()
    {
        var state = new TransitionInverseSetupState();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        state.Enqueue([first, second]);

        Assert.True(state.TryDequeue(out var actualFirst));
        Assert.True(state.TryDequeue(out var actualSecond));
        Assert.Equal(first, actualFirst);
        Assert.Equal(second, actualSecond);
        Assert.False(state.TryDequeue(out _));
    }

    [Fact]
    public void CheckNames_FlagLocalDuplicatesAsDangerAndGlobalDuplicatesAsWarning()
    {
        var roomId = Guid.NewGuid();
        var check = new CheckLocation { RoomId = roomId, FriendlyName = "Check" };

        Assert.Equal(ValidationSeverity.Warning, validation.CheckFriendlyName(new CheckLocation(), []));
        Assert.Equal(ValidationSeverity.Danger, validation.CheckFriendlyName(check, [check, new CheckLocation { RoomId = roomId, FriendlyName = " check " }]));
        Assert.Equal(ValidationSeverity.Warning, validation.CheckFriendlyName(check, [check, new CheckLocation { RoomId = Guid.NewGuid(), FriendlyName = " check " }]));
    }

    [Fact]
    public void SubroomReferencePresence_FlagsAllSupportedFieldsWhenPresenceDoesNotMatchRoomState()
    {
        var roomId = Guid.NewGuid();
        var activeSubroom = new Subroom { RoomId = roomId };
        var transition = new RoomTransition { RoomId = roomId };
        var connection = new SubroomConnection { RoomId = roomId };
        var check = new CheckLocation { RoomId = roomId };

        Assert.Equal(ValidationSeverity.Warning, validation.TransitionSourceSubroom(transition, [activeSubroom]));
        Assert.Equal(ValidationSeverity.Warning, validation.ConnectionSourceSubroom(connection, [activeSubroom]));
        Assert.Equal(ValidationSeverity.Warning, validation.ConnectionDestinationSubroom(connection, [activeSubroom]));
        Assert.Equal(ValidationSeverity.Warning, validation.CheckSubroom(check, [activeSubroom]));

        transition.SourceSubroomReferenceText = "sub";
        connection.SourceSubroomReferenceText = "sub";
        connection.DestinationSubroomReferenceText = "sub";
        check.SubroomReferenceText = "sub";
        Assert.Equal(ValidationSeverity.Warning, validation.TransitionSourceSubroom(transition, []));
        Assert.Equal(ValidationSeverity.Warning, validation.ConnectionSourceSubroom(connection, []));
        Assert.Equal(ValidationSeverity.Warning, validation.ConnectionDestinationSubroom(connection, []));
        Assert.Equal(ValidationSeverity.Warning, validation.CheckSubroom(check, []));
    }

    [Fact]
    public void Connections_FlagMissingInvalidAndNoSubroomViolations()
    {
        var connection = new SubroomConnection { Alias = "long" };

        Assert.Equal(ValidationSeverity.Warning, validation.ConnectionAlias(new SubroomConnection()));
        Assert.Equal(ValidationSeverity.Danger, validation.ConnectionAlias(connection));
        Assert.Equal(ValidationSeverity.Warning, validation.ConnectionFriendlyName(new SubroomConnection()));
        Assert.Equal(ValidationSeverity.Danger, validation.ConnectionWithoutSubrooms(connection, []));
        connection.IsArchived = true;
        Assert.Equal(ValidationSeverity.Neutral, validation.ConnectionWithoutSubrooms(connection, []));
    }

    [Fact]
    public void SceneLayoutGeometry_FlagsIncompleteAndInvalidPersistedValues()
    {
        var room = new Room();
        var subroom = new Subroom();
        var connection = new SubroomConnection();

        Assert.Equal(ValidationSeverity.Neutral, validation.RoomSceneDimensions(room));
        room.SceneUnitWidth = 10;
        Assert.Equal(ValidationSeverity.Danger, validation.RoomSceneDimensions(room));
        room.SceneUnitHeight = 5;
        Assert.Equal(ValidationSeverity.Neutral, validation.RoomSceneDimensions(room));

        Assert.Equal(ValidationSeverity.Neutral, validation.SubroomSceneGeometry(subroom));
        subroom.SceneUnitX = 1;
        Assert.Equal(ValidationSeverity.Danger, validation.SubroomSceneGeometry(subroom));
        subroom.SceneUnitY = 2;
        subroom.SceneUnitWidth = 3;
        subroom.SceneUnitHeight = -1;
        Assert.Equal(ValidationSeverity.Danger, validation.SubroomSceneGeometry(subroom));
        subroom.SceneUnitHeight = 4;
        Assert.Equal(ValidationSeverity.Neutral, validation.SubroomSceneGeometry(subroom));

        connection.SceneUnitX = 1;
        Assert.Equal(ValidationSeverity.Danger, validation.ConnectionSceneAnnotation(connection));
        connection.EnableAnnotation = true;
        Assert.Equal(ValidationSeverity.Danger, validation.ConnectionSceneAnnotation(connection));
        connection.SceneUnitY = 2;
        Assert.Equal(ValidationSeverity.Neutral, validation.ConnectionSceneAnnotation(connection));

        var transition = new RoomTransition { AnnotationSceneUnitX = 1 };
        var check = new CheckLocation { AnnotationSceneUnitX = double.NaN, AnnotationSceneUnitY = 2 };
        Assert.Equal(ValidationSeverity.Danger, validation.TransitionAnnotationPosition(transition));
        Assert.Equal(ValidationSeverity.Danger, validation.CheckAnnotationPosition(check));
        transition.AnnotationSceneUnitY = 2;
        check.AnnotationSceneUnitX = 1;
        Assert.Equal(ValidationSeverity.Neutral, validation.TransitionAnnotationPosition(transition));
        Assert.Equal(ValidationSeverity.Neutral, validation.CheckAnnotationPosition(check));
    }

    [Theory]
    [MemberData(nameof(InvalidPathways))]
    public void ConnectionPathways_FlagEveryTopologyViolation(SubroomConnection connection, SubroomConnection[] connections)
    {
        Assert.Equal(ValidationSeverity.Danger, validation.ConnectionPathway(connection, connections));
    }

    [Fact]
    public void ConnectionTopology_UsesNormalizedAuthoredTextFallbackWhenIdsAreUnavailable()
    {
        var connection = new SubroomConnection { Id = Guid.NewGuid(), Alias = " C ", SourceSubroomReferenceText = " Source ", DestinationSubroomReferenceText = "Destination" };
        var reverse = new SubroomConnection { Id = Guid.NewGuid(), Alias = "c", SourceSubroomReferenceText = " destination ", DestinationSubroomReferenceText = "source" };

        Assert.Equal("bidirectional", validation.ConnectionState(connection, [connection, reverse]));
    }

    [Fact]
    public void InverseScaffolding_IsAvailableOnlyForValidResolvedOneWayNonSelfLoopPathways()
    {
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var connection = ResolvedConnection(sourceId, destinationId);

        Assert.True(validation.CanScaffoldInverse(connection, [connection]));

        connection.IsArchived = true;
        Assert.False(validation.CanScaffoldInverse(connection, [connection]));
        connection.IsArchived = false;

        connection.ResolvedDestinationSubroomId = null;
        Assert.False(validation.CanScaffoldInverse(connection, [connection]));
        connection.ResolvedDestinationSubroomId = destinationId;

        connection.ResolvedDestinationSubroomId = sourceId;
        Assert.False(validation.CanScaffoldInverse(connection, [connection]));
        connection.ResolvedDestinationSubroomId = destinationId;

        var reverse = ResolvedConnection(destinationId, sourceId);
        Assert.False(validation.CanScaffoldInverse(connection, [connection, reverse]));
        Assert.False(validation.CanScaffoldInverse(connection, [connection, reverse, ResolvedConnection(destinationId, sourceId)]));

        var duplicateDirection = ResolvedConnection(sourceId, destinationId);
        Assert.False(validation.CanScaffoldInverse(connection, [connection, duplicateDirection]));
    }

    public static IEnumerable<object[]> InvalidPathways()
    {
        var first = Connection("A", "Path", "one", "two");
        yield return [first, new[] { first, Connection("A", "Other", "two", "one") }];

        first = Connection("A", "Path", "one", "two");
        yield return [first, new[] { first, Connection("A", "Path", "one", "three") }];

        first = Connection("A", "Path", "one", "two");
        yield return [first, new[] { first, Connection("B", "Path", "two", "one") }];

        first = Connection("A", "Path", "one", "two");
        yield return [first, new[] { first, Connection("A", "Path", "two", "one"), Connection("A", "Path", "one", "two") }];

        first = Connection("A", "Path", "one", "two");
        yield return [first, new[] { first, Connection("A", "Path", "one", "two") }];
    }

    private static SubroomConnection Connection(string alias, string name, string source, string destination) =>
        new() { Id = Guid.NewGuid(), RoomId = Guid.Empty, Alias = alias, FriendlyName = name, SourceSubroomReferenceText = source, DestinationSubroomReferenceText = destination };

    private static SubroomConnection ResolvedConnection(Guid sourceId, Guid destinationId) =>
        new()
        {
            Id = Guid.NewGuid(),
            RoomId = Guid.Empty,
            Alias = "A",
            FriendlyName = "Path",
            SourceSubroomReferenceText = "source",
            DestinationSubroomReferenceText = "destination",
            ResolvedSourceSubroomId = sourceId,
            ResolvedDestinationSubroomId = destinationId
        };

    private static RoomTransition TransitionWithResolvedDestination() =>
        new()
        {
            Id = Guid.NewGuid(),
            RoomId = Guid.NewGuid(),
            ResolvedDestinationRoomId = Guid.NewGuid(),
            ResolvedDestinationTransitionId = Guid.NewGuid()
        };

    private static RoomTransition ResolvedInverse(RoomTransition source) =>
        new()
        {
            Id = Guid.NewGuid(),
            RoomId = source.ResolvedDestinationRoomId!.Value,
            ResolvedDestinationRoomId = source.RoomId,
            ResolvedDestinationTransitionId = source.Id
        };
}
