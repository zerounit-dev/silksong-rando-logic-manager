using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using System.Data.Common;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2LogicLoaderTests
{
    [Fact]
    public async Task LoadAsync_UsesMappedScalarRoomPresentation_WithOrderingArchivesAndDiagnostics()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "Room", ReferenceId = "room", InGameId = "scene", Contributors = "Alice" };
        var archivedRoom = new Room { Id = Guid.NewGuid(), FriendlyName = "Old", ReferenceId = "old", IsArchived = true };
        var subA = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "A", ReferenceId = "a", SortOrder = 2 };
        var subB = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "B", ReferenceId = "b", SortOrder = 1 };
        var archivedSub = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "Old", ReferenceId = "old", SortOrder = 0, IsArchived = true };
        var target = new Room { Id = Guid.NewGuid(), FriendlyName = "Target", ReferenceId = "target" };
        var targetExitId = Guid.NewGuid();
        var exit = new RoomTransition { Id = Guid.NewGuid(), RoomId = room.Id, Alias = "x", FriendlyName = "Exit", SourceSubroomReferenceText = "a", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "y", Requirements = "", ResolvedSourceSubroomId = subA.Id, ResolvedDestinationRoomId = target.Id, ResolvedDestinationTransitionId = targetExitId, SortOrder = 1 };
        var targetExit = new RoomTransition { Id = targetExitId, RoomId = target.Id, Alias = "y", FriendlyName = "Return", SourceSubroomReferenceText = "", DestinationRoomReferenceText = "room", DestinationTransitionAliasText = "x", Requirements = "req", ResolvedDestinationRoomId = room.Id, ResolvedDestinationTransitionId = exit.Id };
        var connection = new SubroomConnection { Id = Guid.NewGuid(), RoomId = room.Id, Alias = "p", FriendlyName = "Path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "req", ResolvedSourceSubroomId = subA.Id, ResolvedDestinationSubroomId = subB.Id };
        var check = new CheckLocation { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "Check", SubroomReferenceText = "a", Requirements = "", ResolvedSubroomId = subA.Id };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, archivedRoom, target, subA, subB, archivedSub, connection, check); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDbContext()) { db.AddRange(exit, targetExit); exit.ResolvedDestinationTransitionId = null; targetExit.ResolvedDestinationTransitionId = null; await db.SaveChangesAsync(); exit.ResolvedDestinationTransitionId = targetExit.Id; targetExit.ResolvedDestinationTransitionId = exit.Id; await db.SaveChangesAsync(); }

        var view = await new RoomEditorV2LogicLoader(fixture).LoadAsync(room.Id, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal("Alice", view!.Header.Contributors);
        Assert.Equal(["B", "A"], view!.Subrooms.ActiveRows.Select(x => x.FriendlyName));
        Assert.Single(view.Subrooms.ArchivedRows);
        Assert.Contains("target", view.Transitions.RoomReferenceSuggestions);
        Assert.Equal(V2InverseState.One, Assert.Single(view.Transitions.ActiveRows).InverseState);
        Assert.Equal(V2Severity.Warning, Assert.Single(view.Transitions.ActiveRows).RequirementsSeverity);
        Assert.Equal("one-way", Assert.Single(view.Connections.ActiveRows).PathwayState);
        Assert.Equal(V2Severity.Warning, Assert.Single(view.Checks.ActiveRows).RequirementsSeverity);
    }

    [Fact]
    public async Task LoadAsync_MissingAndArchivedRoutes_AreDistinct()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "Archived", ReferenceId = "archived", IsArchived = true };
        await using (var db = fixture.CreateDbContext()) { db.Add(room); await db.SaveChangesAsync(); }
        var loader = new RoomEditorV2LogicLoader(fixture);
        Assert.Null(await loader.LoadAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True((await loader.LoadAsync(room.Id, CancellationToken.None))!.Header.IsArchived);
    }

    [Fact]
    public async Task LoadAsync_BlankResolvedTargetEnablesInverseAuthoringWithoutAnInverseStateGate()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source" };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target" };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "in", Requirements = "r", ResolvedDestinationRoomId = targetRoom.Id };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(sourceRoom, targetRoom, source, target);
            await db.SaveChangesAsync();
            source.ResolvedDestinationTransitionId = target.Id;
            await db.SaveChangesAsync();
        }

        var loader = new RoomEditorV2LogicLoader(fixture);
        var noInverse = Assert.Single((await loader.LoadAsync(sourceRoom.Id, CancellationToken.None))!.Transitions.ActiveRows);
        Assert.Equal(V2InverseState.Zero, noInverse.InverseState);
        Assert.True(noInverse.CanExplicitInverseSetup);

        await using (var db = fixture.CreateDbContext())
        {
            var durableTarget = await db.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            durableTarget.ResolvedDestinationRoomId = sourceRoom.Id;
            durableTarget.ResolvedDestinationTransitionId = source.Id;
            await db.SaveChangesAsync();
        }
        var oneInverse = Assert.Single((await loader.LoadAsync(sourceRoom.Id, CancellationToken.None))!.Transitions.ActiveRows);
        Assert.Equal(V2InverseState.One, oneInverse.InverseState);
        Assert.True(oneInverse.CanExplicitInverseSetup);
    }

    [Fact]
    public async Task LoadAsync_SubroomFriendlyNameSeverityUsesActiveRowsAndRefreshesAcrossArchiveRestore()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "Room", ReferenceId = "room" };
        var blank = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = " ", ReferenceId = "blank", SortOrder = 0 };
        var firstDuplicate = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "Duplicate", ReferenceId = "first", SortOrder = 1 };
        var secondDuplicate = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = " duplicate ", ReferenceId = "second", SortOrder = 2 };
        var ordinary = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "Ordinary", ReferenceId = "ordinary", SortOrder = 3 };
        var archived = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "Duplicate", ReferenceId = "archived", SortOrder = 0, IsArchived = true };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, blank, firstDuplicate, secondDuplicate, ordinary, archived); await db.SaveChangesAsync(); }
        var loader = new RoomEditorV2LogicLoader(fixture);

        var initial = (await loader.LoadAsync(room.Id, CancellationToken.None))!;
        Assert.Equal(V2Severity.Warning, initial.Subrooms.ActiveRows.Single(x => x.EntityId == blank.Id).FriendlyNameSeverity);
        Assert.Equal(V2Severity.Danger, initial.Subrooms.ActiveRows.Single(x => x.EntityId == firstDuplicate.Id).FriendlyNameSeverity);
        Assert.Equal(V2Severity.Danger, initial.Subrooms.ActiveRows.Single(x => x.EntityId == secondDuplicate.Id).FriendlyNameSeverity);
        Assert.Equal(V2Severity.Neutral, initial.Subrooms.ActiveRows.Single(x => x.EntityId == ordinary.Id).FriendlyNameSeverity);
        Assert.Equal(V2Severity.Neutral, Assert.Single(initial.Subrooms.ArchivedRows).FriendlyNameSeverity);

        await using (var db = fixture.CreateDbContext()) { (await db.Subrooms.SingleAsync(x => x.Id == secondDuplicate.Id)).IsArchived = true; await db.SaveChangesAsync(); }
        var archivedRefresh = RoomEditorV2Mapper.Reconcile(initial, (await loader.LoadAsync(room.Id, CancellationToken.None))!);
        Assert.Equal(V2Severity.Neutral, archivedRefresh.Subrooms.ActiveRows.Single(x => x.EntityId == firstDuplicate.Id).FriendlyNameSeverity);
        Assert.Equal(V2Severity.Neutral, archivedRefresh.Subrooms.ArchivedRows.Single(x => x.EntityId == secondDuplicate.Id).FriendlyNameSeverity);

        await using (var db = fixture.CreateDbContext()) { (await db.Subrooms.SingleAsync(x => x.Id == secondDuplicate.Id)).IsArchived = false; await db.SaveChangesAsync(); }
        var restoredRefresh = RoomEditorV2Mapper.Reconcile(archivedRefresh, (await loader.LoadAsync(room.Id, CancellationToken.None))!);
        Assert.Equal(V2Severity.Danger, restoredRefresh.Subrooms.ActiveRows.Single(x => x.EntityId == firstDuplicate.Id).FriendlyNameSeverity);
        Assert.Equal(V2Severity.Danger, restoredRefresh.Subrooms.ActiveRows.Single(x => x.EntityId == secondDuplicate.Id).FriendlyNameSeverity);
    }

    [Fact]
    public async Task LoadAsync_UsesFourteenBoundedScalarNoTrackingQueries_ForOnlyLogicalRoomInputs()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "selected", ReferenceId = "selected" };
        var other = new Room { Id = Guid.NewGuid(), FriendlyName = "other", ReferenceId = "other" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(room, other,
                new Subroom { RoomId = room.Id, FriendlyName = "sub", ReferenceId = "sub" },
                new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r" },
                new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "path", Requirements = "r" },
                new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r" },
                new CheckLocation { RoomId = other.Id, FriendlyName = "global fact", Requirements = "r" });
            await db.SaveChangesAsync();
        }

        var commands = new ReaderCapture();
        var factory = new InspectingFactory(fixture.DatabasePath, commands);
        var view = await new RoomEditorV2LogicLoader(factory).LoadAsync(room.Id, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(14, commands.Commands.Count); // the existing twelve room readers plus one bounded flat predicate reader and one bounded flat item reader
        Assert.Equal(0, commands.EntityMaterializations);
        Assert.All(commands.Commands, sql =>
        {
            Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Single(commands.Commands, sql => sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase)); // the existing active-check/active-room validation fact join remains one bounded scalar reader
        // Child rows remain selected-room scoped. The remaining validation queries
        // are targeted by selected values/IDs, never a catalogue-wide child index.
        Assert.True(commands.Commands.Count(sql => sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase) && sql.Contains("RoomId", StringComparison.OrdinalIgnoreCase)) >= 6);
        Assert.DoesNotContain(commands.Commands, sql => sql.Contains("MapChunk", StringComparison.OrdinalIgnoreCase) || sql.Contains("RoomGroup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_CheckOnlySelectedRoom_IgnoresCrossRoomInGameIdConflict()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var selected = new Room { Id = Guid.NewGuid(), FriendlyName = "selected", ReferenceId = "selected" };
        var other = new Room { Id = Guid.NewGuid(), FriendlyName = "other", ReferenceId = "other" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(selected, other,
                new CheckLocation { RoomId = selected.Id, FriendlyName = "check", Requirements = "r", InGameId = " shared-id " },
                new RoomTransition { RoomId = other.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", InGameId = "SHARED-ID" });
            await db.SaveChangesAsync();
        }

        var commands = new ReaderCapture();
        var view = await new RoomEditorV2LogicLoader(new InspectingFactory(fixture.DatabasePath, commands)).LoadAsync(selected.Id, CancellationToken.None);

        Assert.Equal(V2Severity.Neutral, Assert.Single(view!.Checks.ActiveRows).InGameIdSeverity);
        Assert.Equal(14, commands.Commands.Count);
        Assert.Equal(0, commands.EntityMaterializations);
    }

    [Fact]
    public async Task LoadAsync_CheckCrossRoomTransitionConflict_IsNeutral()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var selected = new Room { Id = Guid.NewGuid(), FriendlyName = "selected", ReferenceId = "selected" };
        var other = new Room { Id = Guid.NewGuid(), FriendlyName = "other", ReferenceId = "other" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(selected, other,
                new CheckLocation { RoomId = selected.Id, FriendlyName = "check", Requirements = "r", InGameId = "check-id" },
                new RoomTransition { RoomId = other.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", InGameId = "check-id" },
                new RoomTransition { RoomId = selected.Id, Alias = "b", FriendlyName = "different", Requirements = "r", InGameId = "other-id" });
            await db.SaveChangesAsync();
        }

        var view = await new RoomEditorV2LogicLoader(fixture).LoadAsync(selected.Id, CancellationToken.None);

        Assert.Equal(V2Severity.Neutral, Assert.Single(view!.Checks.ActiveRows).InGameIdSeverity);
    }

    [Fact]
    public async Task MapperReconcile_CompletelyReplacesPersistedAndDerivedPhaseOneView()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "before", ReferenceId = "before", Comments = "old" };
        var active = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "old active", ReferenceId = "a", SortOrder = 2 };
        var archived = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "old archived", ReferenceId = "z", SortOrder = 1, IsArchived = true };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, active, archived); await db.SaveChangesAsync(); }
        var loader = new RoomEditorV2LogicLoader(fixture);
        var before = (await loader.LoadAsync(room.Id, CancellationToken.None))!;
        await using (var db = fixture.CreateDbContext())
        {
            var persistedRoom = await db.Rooms.SingleAsync(x => x.Id == room.Id);
            persistedRoom.FriendlyName = "after"; persistedRoom.ReferenceId = "after"; persistedRoom.Comments = "new"; persistedRoom.IsArchived = true;
            var persistedActive = await db.Subrooms.SingleAsync(x => x.Id == active.Id);
            persistedActive.FriendlyName = "moved"; persistedActive.ReferenceId = "m"; persistedActive.Notes = "notes"; persistedActive.SortOrder = 9; persistedActive.IsArchived = true;
            var persistedArchived = await db.Subrooms.SingleAsync(x => x.Id == archived.Id);
            persistedArchived.FriendlyName = "restored"; persistedArchived.IsArchived = false; persistedArchived.SortOrder = 0;
            db.Subrooms.Add(new Subroom { RoomId = room.Id, FriendlyName = "added", ReferenceId = "n", SortOrder = 1 });
            await db.SaveChangesAsync();
        }
        var fresh = (await loader.LoadAsync(room.Id, CancellationToken.None))!;
        var reconciled = RoomEditorV2Mapper.Reconcile(before, fresh);

        Assert.Equal(fresh, reconciled);
        Assert.Equal("after", reconciled.Header.FriendlyName); Assert.True(reconciled.Header.IsArchived);
        Assert.Equal(["restored", "added"], reconciled.Subrooms.ActiveRows.Select(x => x.FriendlyName));
        Assert.Equal(["moved"], reconciled.Subrooms.ArchivedRows.Select(x => x.FriendlyName));
        Assert.Equal("notes", reconciled.Subrooms.ArchivedRows[0].Notes);
        Assert.DoesNotContain(reconciled.Subrooms.ActiveRows.Concat(reconciled.Subrooms.ArchivedRows), x => x.FriendlyName == "old active");
    }

    [Fact]
    public async Task LoadAsync_ParityMatrix_ProjectsReferenceInversePathwayAndSeverityStates()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "Room", ReferenceId = "room" };
        var target = new Room { Id = Guid.NewGuid(), FriendlyName = "Target", ReferenceId = "target" };
        var archivedTarget = new Room { Id = Guid.NewGuid(), FriendlyName = "Old", ReferenceId = "old", IsArchived = true };
        var a = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var transition = new RoomTransition { Id = Guid.NewGuid(), RoomId = room.Id, Alias = "x", FriendlyName = "Exit", SourceSubroomReferenceText = "a", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "y", Requirements = "r", ResolvedSourceSubroomId = a.Id, ResolvedDestinationRoomId = target.Id };
        var targetExit = new RoomTransition { Id = Guid.NewGuid(), RoomId = target.Id, Alias = "y", FriendlyName = "Return", DestinationRoomReferenceText = "room", DestinationTransitionAliasText = "x", Requirements = "r", ResolvedDestinationRoomId = room.Id, ResolvedDestinationTransitionId = transition.Id };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(room, target, archivedTarget, a, b, transition, targetExit,
                new RoomTransition { RoomId = room.Id, Alias = "x", FriendlyName = "Exit", Requirements = "", InGameId = "shared" },
                new RoomTransition { RoomId = room.Id, Alias = "q", FriendlyName = "Archived target", DestinationRoomReferenceText = "old", DestinationTransitionAliasText = "z", Requirements = "r" },
                new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "Path", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", ResolvedSourceSubroomId = a.Id, ResolvedDestinationSubroomId = b.Id },
                new SubroomConnection { RoomId = room.Id, Alias = "p", FriendlyName = "Path", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", Requirements = "r", ResolvedSourceSubroomId = b.Id, ResolvedDestinationSubroomId = a.Id },
                new SubroomConnection { RoomId = room.Id, Alias = "bad", FriendlyName = "Bad", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", ResolvedSourceSubroomId = a.Id, ResolvedDestinationSubroomId = b.Id },
                new SubroomConnection { RoomId = room.Id, Alias = "bad", FriendlyName = "Other", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", ResolvedSourceSubroomId = a.Id, ResolvedDestinationSubroomId = b.Id },
                new CheckLocation { RoomId = room.Id, FriendlyName = "Dup", Requirements = "", InGameId = "shared", AnnotationSceneUnitX = 1 },
                new CheckLocation { RoomId = room.Id, FriendlyName = "Dup", Requirements = "r" },
                new CheckLocation { RoomId = target.Id, FriendlyName = "Dup", Requirements = "r" });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateDbContext()) { (await db.RoomTransitions.SingleAsync(x => x.Id == transition.Id)).ResolvedDestinationTransitionId = targetExit.Id; await db.SaveChangesAsync(); }
        var view = (await new RoomEditorV2LogicLoader(fixture).LoadAsync(room.Id, CancellationToken.None))!;
        var resolved = view.Transitions.ActiveRows.Single(x => x.EntityId == transition.Id);
        Assert.Equal(V2ReferenceState.Resolved, resolved.DestinationRoomState); Assert.Equal(V2ReferenceState.Resolved, resolved.DestinationAliasState); Assert.Equal(V2InverseState.One, resolved.InverseState);
        Assert.Equal(V2ReferenceState.TargetArchived, view.Transitions.ActiveRows.Single(x => x.Alias == "q").DestinationRoomState);
        Assert.All(view.Connections.ActiveRows.Where(x => x.Alias == "p"), x => Assert.Equal("bidirectional", x.PathwayState));
        Assert.All(view.Connections.ActiveRows.Where(x => x.Alias == "bad"), x => Assert.Equal(V2Severity.Danger, x.PathwaySeverity));
        Assert.All(view.Checks.ActiveRows.Where(x => x.FriendlyName == "Dup"), x => Assert.Equal(V2Severity.Danger, x.FriendlyNameSeverity));
        Assert.Contains(view.Checks.ActiveRows, x => x.InGameIdSeverity == V2Severity.Danger && x.PositionSeverity == V2Severity.Danger);
    }

    [Fact]
    public async Task LoadAsync_ActiveAndArchivedSameTextRoomCandidate_ResolvesTheActiveCandidate()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = new Room { Id = Guid.NewGuid(), FriendlyName = "Source", ReferenceId = "source" };
        var active = new Room { Id = Guid.NewGuid(), FriendlyName = "Active", ReferenceId = "target" };
        var archived = new Room { Id = Guid.NewGuid(), FriendlyName = "Archived", ReferenceId = "target", IsArchived = true };
        var transition = new RoomTransition { Id = Guid.NewGuid(), RoomId = source.Id, Alias = "x", FriendlyName = "Exit", DestinationRoomReferenceText = "target", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(source, active, archived, transition); await db.SaveChangesAsync(); transition.ResolvedDestinationRoomId = active.Id; await db.SaveChangesAsync(); }

        var row = Assert.Single((await new RoomEditorV2LogicLoader(fixture).LoadAsync(source.Id, CancellationToken.None))!.Transitions.ActiveRows);

        Assert.Equal(V2ReferenceState.Resolved, row.DestinationRoomState);
        Assert.Equal(V2Severity.Neutral, row.DestinationRoomSeverity);
    }

    [Fact]
    public async Task LoadAsync_ArchivedOnlyRoomCandidate_IsArchivedTargetWithDangerSeverity()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = new Room { Id = Guid.NewGuid(), FriendlyName = "Source", ReferenceId = "source" };
        var archived = new Room { Id = Guid.NewGuid(), FriendlyName = "Archived", ReferenceId = "target", IsArchived = true };
        var transition = new RoomTransition { RoomId = source.Id, Alias = "x", FriendlyName = "Exit", DestinationRoomReferenceText = "target", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(source, archived, transition); await db.SaveChangesAsync(); }

        var row = Assert.Single((await new RoomEditorV2LogicLoader(fixture).LoadAsync(source.Id, CancellationToken.None))!.Transitions.ActiveRows);

        Assert.Equal(V2ReferenceState.TargetArchived, row.DestinationRoomState);
        Assert.Equal(V2Severity.Danger, row.DestinationRoomSeverity);
    }

    [Fact]
    public async Task LoadAsync_ActiveDuplicateRoomCandidates_AreAmbiguousWithDangerSeverity()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = new Room { Id = Guid.NewGuid(), FriendlyName = "Source", ReferenceId = "source" };
        var first = new Room { Id = Guid.NewGuid(), FriendlyName = "First", ReferenceId = "target" };
        var second = new Room { Id = Guid.NewGuid(), FriendlyName = "Second", ReferenceId = "target" };
        var transition = new RoomTransition { RoomId = source.Id, Alias = "x", FriendlyName = "Exit", DestinationRoomReferenceText = "target", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(source, first, second, transition); await db.SaveChangesAsync(); }

        var row = Assert.Single((await new RoomEditorV2LogicLoader(fixture).LoadAsync(source.Id, CancellationToken.None))!.Transitions.ActiveRows);

        Assert.Equal(V2ReferenceState.Ambiguous, row.DestinationRoomState);
        Assert.Equal(V2Severity.Danger, row.DestinationRoomSeverity);
    }

    [Fact]
    public async Task LoadAsync_StaleRoomResolverId_IsOutOfSyncWithDangerSeverity()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = new Room { Id = Guid.NewGuid(), FriendlyName = "Source", ReferenceId = "source" };
        var target = new Room { Id = Guid.NewGuid(), FriendlyName = "Target", ReferenceId = "target" };
        var stale = new Room { Id = Guid.NewGuid(), FriendlyName = "Stale", ReferenceId = "stale" };
        var transition = new RoomTransition { Id = Guid.NewGuid(), RoomId = source.Id, Alias = "x", FriendlyName = "Exit", DestinationRoomReferenceText = "target", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(source, target, stale, transition); await db.SaveChangesAsync(); transition.ResolvedDestinationRoomId = stale.Id; await db.SaveChangesAsync(); }

        var row = Assert.Single((await new RoomEditorV2LogicLoader(fixture).LoadAsync(source.Id, CancellationToken.None))!.Transitions.ActiveRows);

        Assert.Equal(V2ReferenceState.OutOfSync, row.DestinationRoomState);
        Assert.Equal(V2Severity.Danger, row.DestinationRoomSeverity);
    }

    [Fact]
    public async Task LoadAsync_DestinationAliasSuggestions_AreRowOwnedAndActiveDestinationScoped()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = new Room { Id = Guid.NewGuid(), FriendlyName = "Source", ReferenceId = "source" };
        var target = new Room { Id = Guid.NewGuid(), FriendlyName = "Target", ReferenceId = "target" };
        var other = new Room { Id = Guid.NewGuid(), FriendlyName = "Other", ReferenceId = "other" };
        var exit = new RoomTransition { Id = Guid.NewGuid(), RoomId = source.Id, Alias = "x", FriendlyName = "Exit", DestinationRoomReferenceText = "target", Requirements = "r" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(source, target, other, exit,
                new RoomTransition { RoomId = target.Id, Alias = "a", FriendlyName = "Active target", Requirements = "r" },
                new RoomTransition { RoomId = target.Id, Alias = "z", FriendlyName = "Archived target", Requirements = "r", IsArchived = true },
                new RoomTransition { RoomId = source.Id, Alias = "s", FriendlyName = "Source", Requirements = "r" },
                new RoomTransition { RoomId = other.Id, Alias = "o", FriendlyName = "Other", Requirements = "r" });
            await db.SaveChangesAsync();
            exit.ResolvedDestinationRoomId = target.Id;
            await db.SaveChangesAsync();
        }

        var suggestions = (await new RoomEditorV2LogicLoader(fixture).LoadAsync(source.Id, CancellationToken.None))!.Transitions.DestinationAliasSuggestionsByTransitionId[exit.Id];

        Assert.Equal(["a"], suggestions);
    }

    [Fact]
    public async Task LoadAsync_TransitionSourceSuggestionsAndConnectionTailUseOnlySortedActiveSubrooms()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var activeRoom = new Room { Id = Guid.NewGuid(), FriendlyName = "Active", ReferenceId = "active" };
        var emptyRoom = new Room { Id = Guid.NewGuid(), FriendlyName = "Empty", ReferenceId = "empty" };
        var archivedOnlyRoom = new Room { Id = Guid.NewGuid(), FriendlyName = "Archived only", ReferenceId = "archived-only" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(
                activeRoom,
                emptyRoom,
                archivedOnlyRoom,
                new Subroom { RoomId = activeRoom.Id, FriendlyName = "Zulu", ReferenceId = "zulu", SortOrder = 1 },
                new Subroom { RoomId = activeRoom.Id, FriendlyName = "Alpha", ReferenceId = "alpha", SortOrder = 2 },
                new Subroom { RoomId = activeRoom.Id, FriendlyName = "Archived", ReferenceId = "beta", SortOrder = 0, IsArchived = true },
                new Subroom { RoomId = archivedOnlyRoom.Id, FriendlyName = "Archived", ReferenceId = "old", IsArchived = true });
            await db.SaveChangesAsync();
        }

        var loader = new RoomEditorV2LogicLoader(fixture);
        var active = (await loader.LoadAsync(activeRoom.Id, CancellationToken.None))!;
        var empty = (await loader.LoadAsync(emptyRoom.Id, CancellationToken.None))!;
        var archivedOnly = (await loader.LoadAsync(archivedOnlyRoom.Id, CancellationToken.None))!;

        Assert.Equal(["alpha", "zulu"], active.Transitions.SubroomReferenceSuggestions);
        Assert.True(active.Connections.CanRenderUncommittedTail);
        Assert.Empty(empty.Transitions.SubroomReferenceSuggestions);
        Assert.False(empty.Connections.CanRenderUncommittedTail);
        Assert.Empty(archivedOnly.Transitions.SubroomReferenceSuggestions);
        Assert.False(archivedOnly.Connections.CanRenderUncommittedTail);
    }

    private sealed class InspectingFactory(string path, ReaderCapture capture) : IDbContextFactory<LogicDbContext>
    {
        public List<LogicDbContext> Contexts { get; } = [];
        public LogicDbContext CreateDbContext() { var db = new LogicDbContext(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").AddInterceptors(capture).Options); Contexts.Add(db); return db; }
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class ReaderCapture : DbCommandInterceptor, IMaterializationInterceptor
    {
        public List<string> Commands { get; } = [];
        public int EntityMaterializations { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity) { EntityMaterializations++; return entity; }
    }
}
