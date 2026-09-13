using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class LogicDbContextTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-logic-tests-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveChanges_AssignsUtcAuditTimestamps()
    {
        await using var db = CreateContext();
        var group = new RoomGroup { FriendlyName = "Bone Bottom", SortOrder = 0 };

        db.RoomGroups.Add(group);
        await db.SaveChangesAsync();

        Assert.NotEqual(default, group.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, group.CreatedUtc.Kind);
        Assert.Equal(group.CreatedUtc, group.UpdatedUtc);
    }

    [Fact]
    public async Task CreateRoomGroup_PersistsPlaceholderAtTheEndOfTheGroupOrder()
    {
        await using (var db = CreateContext())
        {
            db.RoomGroups.Add(new RoomGroup { FriendlyName = "Existing", SortOrder = 0 });
            await db.SaveChangesAsync();
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var group = await catalog.CreateRoomGroupAsync(" New group ");

        await using var verificationDb = CreateContext();
        var persistedGroup = await verificationDb.RoomGroups.SingleAsync(x => x.Id == group.Id);
        Assert.Equal("New group", persistedGroup.FriendlyName);
        Assert.Equal(1, persistedGroup.SortOrder);
    }

    [Fact]
    public async Task Resolver_PreservesAuthoredTextAndSetsNavigationIds()
    {
        await using var db = CreateContext();
        var source = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var destination = new Room { FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
        db.Rooms.AddRange(source, destination);
        await db.SaveChangesAsync();

        var destinationTransition = new RoomTransition
        {
            RoomId = destination.Id,
            Alias = "IN",
            FriendlyName = "Entrance",
            SortOrder = 0
        };
        var transition = new RoomTransition
        {
            RoomId = source.Id,
            Alias = "OUT",
            FriendlyName = "Exit",
            DestinationRoomReferenceText = " destination ",
            DestinationTransitionAliasText = "in",
            SortOrder = 0
        };
        db.RoomTransitions.AddRange(destinationTransition, transition);
        await db.SaveChangesAsync();

        await new LogicReferenceResolver(db).ResolveAsync();

        Assert.Equal(" destination ", transition.DestinationRoomReferenceText);
        Assert.Equal("in", transition.DestinationTransitionAliasText);
        Assert.Equal(destination.Id, transition.ResolvedDestinationRoomId);
        Assert.Equal(destinationTransition.Id, transition.ResolvedDestinationTransitionId);
    }

    [Fact]
    public async Task CatalogSave_UnchangedEntityDoesNotWriteOrUpdateTimestamp()
    {
        Guid groupId;
        DateTime updatedUtc;
        await using (var db = CreateContext())
        {
            var group = new RoomGroup { FriendlyName = "Bone Bottom", SortOrder = 0 };
            db.RoomGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
            updatedUtc = group.UpdatedUtc;
        }

        RoomGroup detachedGroup;
        await using (var db = CreateContext())
        {
            detachedGroup = await db.RoomGroups.AsNoTracking().SingleAsync(x => x.Id == groupId);
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var wasSaved = await catalog.SaveAsync(detachedGroup);

        await using var verificationDb = CreateContext();
        var persistedGroup = await verificationDb.RoomGroups.SingleAsync(x => x.Id == groupId);
        Assert.False(wasSaved);
        Assert.Equal(updatedUtc, persistedGroup.UpdatedUtc);
    }

    [Fact]
    public async Task CatalogSave_RejectsAStaleDetachedEntityWithoutReversingArchiveState()
    {
        Room detachedRoom;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            detachedRoom = await db.Rooms.AsNoTracking().SingleAsync(x => x.Id == room.Id);
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.SetArchivedAsync(detachedRoom, true);
        detachedRoom.FriendlyName = "Stale name";

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveAsync(detachedRoom));

        await using var verificationDb = CreateContext();
        var persistedRoom = await verificationDb.Rooms.SingleAsync(x => x.Id == detachedRoom.Id);
        Assert.True(persistedRoom.IsArchived);
        Assert.Equal("Room", persistedRoom.FriendlyName);
    }

    [Fact]
    public async Task DeleteRoomGroup_OrphansActiveAndArchivedRooms()
    {
        Guid groupId;
        await using (var db = CreateContext())
        {
            var group = new RoomGroup { FriendlyName = "Area", SortOrder = 0 };
            var activeRoom = new Room { FriendlyName = "Active", ReferenceId = "active", RoomGroup = group, SortOrder = 0 };
            var archivedRoom = new Room { FriendlyName = "Archived", ReferenceId = "archived", RoomGroup = group, SortOrder = 1, IsArchived = true };
            db.AddRange(group, activeRoom, archivedRoom);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.DeleteRoomGroupAsync(groupId);

        await using var verificationDb = CreateContext();
        Assert.False(await verificationDb.RoomGroups.AnyAsync(x => x.Id == groupId));
        Assert.All(await verificationDb.Rooms.ToListAsync(), room => Assert.Null(room.RoomGroupId));
    }

    [Fact]
    public async Task ArchiveRoom_ChangesOnlyTheRoom()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            db.Subrooms.Add(new Subroom { RoomId = room.Id, FriendlyName = "Subroom", ReferenceId = "subroom", SortOrder = 0 });
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.SetArchivedAsync(new Room { Id = roomId }, true);

        await using (var verificationDb = CreateContext())
        {
            Assert.True(await verificationDb.Rooms.Where(x => x.Id == roomId).Select(x => x.IsArchived).SingleAsync());
            Assert.False(await verificationDb.Subrooms.Select(x => x.IsArchived).SingleAsync());
        }

        await catalog.SetArchivedAsync(new Room { Id = roomId }, false);
        await using var restoredDb = CreateContext();
        Assert.False(await restoredDb.Rooms.Where(x => x.Id == roomId).Select(x => x.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task DeleteArchivedRoom_ClearsExternalResolverIdsAndAuthoredText()
    {
        Guid targetRoomId;
        Guid sourceTransitionId;
        await using (var db = CreateContext())
        {
            var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 0 };
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 1 };
            db.Rooms.AddRange(targetRoom, sourceRoom);
            await db.SaveChangesAsync();
            db.RoomTransitions.AddRange(
                new RoomTransition { RoomId = targetRoom.Id, Alias = "IN", FriendlyName = "Entrance", SortOrder = 0 },
                new RoomTransition { RoomId = sourceRoom.Id, Alias = "OUT", FriendlyName = "Exit", DestinationRoomReferenceText = " target ", DestinationTransitionAliasText = "in", SortOrder = 0 });
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            targetRoomId = targetRoom.Id;
            sourceTransitionId = await db.RoomTransitions.Where(x => x.RoomId == sourceRoom.Id).Select(x => x.Id).SingleAsync();
            targetRoom.IsArchived = true;
            await db.SaveChangesAsync();
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.DeleteRoomPermanentlyAsync(targetRoomId);

        await using var verificationDb = CreateContext();
        var sourceTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == sourceTransitionId);
        Assert.Equal(" target ", sourceTransition.DestinationRoomReferenceText);
        Assert.Equal("in", sourceTransition.DestinationTransitionAliasText);
        Assert.Null(sourceTransition.ResolvedDestinationRoomId);
        Assert.Null(sourceTransition.ResolvedDestinationTransitionId);
        Assert.False(await verificationDb.Rooms.AnyAsync(x => x.ReferenceId == "target"));
        Assert.False(await verificationDb.RoomTransitions.AnyAsync(x => x.FriendlyName == "Entrance"));
    }

    [Fact]
    public async Task DeleteArchivedSubroom_ClearsInboundResolverIds()
    {
        Guid subroomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var subroom = new Subroom { RoomId = room.Id, FriendlyName = "Subroom", ReferenceId = "subroom", SortOrder = 0, IsArchived = true };
            db.Subrooms.Add(subroom);
            await db.SaveChangesAsync();
            db.RoomTransitions.Add(new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "Transition", SourceSubroomReferenceText = "subroom", SortOrder = 0 });
            db.SubroomConnections.Add(new SubroomConnection { RoomId = room.Id, Alias = "B", FriendlyName = "Connection", SourceSubroomReferenceText = "subroom", DestinationSubroomReferenceText = "subroom", SortOrder = 0 });
            db.CheckLocations.Add(new CheckLocation { RoomId = room.Id, FriendlyName = "Check", SubroomReferenceText = "subroom", SortOrder = 0 });
            await db.SaveChangesAsync();
            subroom.IsArchived = false;
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            subroom.IsArchived = true;
            await db.SaveChangesAsync();
            subroomId = subroom.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.DeleteSubroomPermanentlyAsync(subroomId);

        await using var verificationDb = CreateContext();
        Assert.False(await verificationDb.Subrooms.AnyAsync());
        Assert.Null(await verificationDb.RoomTransitions.Select(x => x.ResolvedSourceSubroomId).SingleAsync());
        Assert.Null(await verificationDb.SubroomConnections.Select(x => x.ResolvedSourceSubroomId).SingleAsync());
        Assert.Null(await verificationDb.SubroomConnections.Select(x => x.ResolvedDestinationSubroomId).SingleAsync());
        Assert.Null(await verificationDb.CheckLocations.Select(x => x.ResolvedSubroomId).SingleAsync());
    }

    [Fact]
    public async Task MoveRoom_UpdatesGroupAssignmentAndAffectedSortOrders()
    {
        Guid movedRoomId;
        Guid targetGroupId;
        await using (var db = CreateContext())
        {
            var sourceGroup = new RoomGroup { FriendlyName = "Source", SortOrder = 0 };
            var targetGroup = new RoomGroup { FriendlyName = "Target", SortOrder = 1 };
            db.RoomGroups.AddRange(sourceGroup, targetGroup);
            await db.SaveChangesAsync();
            var movedRoom = new Room { FriendlyName = "Moved", ReferenceId = "moved", RoomGroupId = sourceGroup.Id, SortOrder = 0 };
            var sourceRoom = new Room { FriendlyName = "Source room", ReferenceId = "source-room", RoomGroupId = sourceGroup.Id, SortOrder = 1 };
            var targetRoom = new Room { FriendlyName = "Target room", ReferenceId = "target-room", RoomGroupId = targetGroup.Id, SortOrder = 0 };
            db.Rooms.AddRange(movedRoom, sourceRoom, targetRoom);
            await db.SaveChangesAsync();
            movedRoomId = movedRoom.Id;
            targetGroupId = targetGroup.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var committedRooms = await catalog.MoveRoomAsync(movedRoomId, targetGroupId, 0);

        await using var verificationDb = CreateContext();
        var targetRooms = await verificationDb.Rooms.Where(x => x.RoomGroupId == targetGroupId).OrderBy(x => x.SortOrder).Select(x => x.FriendlyName).ToListAsync();
        Assert.Equal(["Moved", "Target room"], targetRooms);
        Assert.Equal(0, await verificationDb.Rooms.Where(x => x.ReferenceId == "source-room").Select(x => x.SortOrder).SingleAsync());
        Assert.Equal(["Moved", "Source room", "Target room"], committedRooms.Select(x => x.FriendlyName));
    }

    [Fact]
    public async Task MoveRoom_WithinGroupTerminalDropReturnsCommittedOrder()
    {
        Guid firstRoomId;
        Guid groupId;
        await using (var db = CreateContext())
        {
            var group = new RoomGroup { FriendlyName = "Group", SortOrder = 0 };
            db.RoomGroups.Add(group);
            await db.SaveChangesAsync();
            var first = new Room { FriendlyName = "First", ReferenceId = "first", RoomGroupId = group.Id, SortOrder = 0 };
            db.Rooms.AddRange(
                first,
                new Room { FriendlyName = "Second", ReferenceId = "second", RoomGroupId = group.Id, SortOrder = 1 },
                new Room { FriendlyName = "Third", ReferenceId = "third", RoomGroupId = group.Id, SortOrder = 2 });
            await db.SaveChangesAsync();
            firstRoomId = first.Id;
            groupId = group.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var committedRooms = await catalog.MoveRoomAsync(firstRoomId, groupId, 3);

        Assert.Equal(["Second", "Third", "First"], committedRooms.Select(x => x.FriendlyName));
        Assert.Equal([0, 1, 2], committedRooms.Select(x => x.SortOrder));
    }

    [Fact]
    public async Task MoveChildRows_UpdatesSortOrdersWithinEachChildCollection()
    {
        Guid subroomId;
        Guid transitionId;
        Guid connectionId;
        Guid checkId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            var subrooms = new[]
            {
                new Subroom { RoomId = room.Id, FriendlyName = "First", ReferenceId = "first", SortOrder = 0 },
                new Subroom { RoomId = room.Id, FriendlyName = "Second", ReferenceId = "second", SortOrder = 1 },
                new Subroom { RoomId = room.Id, FriendlyName = "Third", ReferenceId = "third", SortOrder = 2 }
            };
            var transitions = new[]
            {
                new RoomTransition { RoomId = room.Id, FriendlyName = "First", Alias = "A", SortOrder = 0 },
                new RoomTransition { RoomId = room.Id, FriendlyName = "Second", Alias = "B", SortOrder = 1 },
                new RoomTransition { RoomId = room.Id, FriendlyName = "Third", Alias = "C", SortOrder = 2 }
            };
            var connections = new[]
            {
                new SubroomConnection { RoomId = room.Id, FriendlyName = "First", Alias = "A", SourceSubroomReferenceText = "first", DestinationSubroomReferenceText = "second", SortOrder = 0 },
                new SubroomConnection { RoomId = room.Id, FriendlyName = "Second", Alias = "B", SourceSubroomReferenceText = "second", DestinationSubroomReferenceText = "third", SortOrder = 1 },
                new SubroomConnection { RoomId = room.Id, FriendlyName = "Third", Alias = "C", SourceSubroomReferenceText = "third", DestinationSubroomReferenceText = "first", SortOrder = 2 }
            };
            var checks = new[]
            {
                new CheckLocation { RoomId = room.Id, FriendlyName = "First", SortOrder = 0 },
                new CheckLocation { RoomId = room.Id, FriendlyName = "Second", SortOrder = 1 },
                new CheckLocation { RoomId = room.Id, FriendlyName = "Third", SortOrder = 2 }
            };
            db.AddRange(subrooms);
            db.AddRange(transitions);
            db.AddRange(connections);
            db.AddRange(checks);
            await db.SaveChangesAsync();
            subroomId = subrooms[0].Id;
            transitionId = transitions[0].Id;
            connectionId = connections[0].Id;
            checkId = checks[0].Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.MoveSubroomAsync(subroomId, 3);
        await catalog.MoveTransitionAsync(transitionId, 3);
        await catalog.MoveConnectionAsync(connectionId, 3);
        await catalog.MoveCheckAsync(checkId, 3);

        await using var verificationDb = CreateContext();
        Assert.Equal(["Second", "Third", "First"], await verificationDb.Subrooms.OrderBy(x => x.SortOrder).Select(x => x.FriendlyName).ToListAsync());
        Assert.Equal(["Second", "Third", "First"], await verificationDb.RoomTransitions.OrderBy(x => x.SortOrder).Select(x => x.FriendlyName).ToListAsync());
        Assert.Equal(["Second", "Third", "First"], await verificationDb.SubroomConnections.OrderBy(x => x.SortOrder).Select(x => x.FriendlyName).ToListAsync());
        Assert.Equal(["Second", "Third", "First"], await verificationDb.CheckLocations.OrderBy(x => x.SortOrder).Select(x => x.FriendlyName).ToListAsync());
    }

    [Fact]
    public async Task SavePlainTextNotes_PreservesLiteralMarkdownSyntax()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            db.CheckLocations.Add(new CheckLocation { RoomId = room.Id, FriendlyName = "Check", SortOrder = 0 });
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        const string notes = "**literal**\n- not a rendered list";
        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var document = await catalog.GetRoomAsync(roomId);
        var check = Assert.Single(document!.Checks);
        check.Notes = notes;
        await catalog.SaveAsync(check);

        await using var verificationDb = CreateContext();
        Assert.Equal(notes, await verificationDb.CheckLocations.Select(x => x.Notes).SingleAsync());
    }

    [Fact]
    public async Task ArchiveAndRestoreChild_ChangesOnlyTheSelectedRecord()
    {
        Guid roomId;
        Guid subroomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var subroom = new Subroom { RoomId = room.Id, FriendlyName = "Subroom", ReferenceId = "subroom", SortOrder = 0 };
            db.Subrooms.Add(subroom);
            await db.SaveChangesAsync();
            roomId = room.Id;
            subroomId = subroom.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.SetArchivedAsync(new Subroom { Id = subroomId }, true);

        await using (var archivedDb = CreateContext())
        {
            Assert.True(await archivedDb.Subrooms.Where(x => x.Id == subroomId).Select(x => x.IsArchived).SingleAsync());
            Assert.False(await archivedDb.Rooms.Where(x => x.Id == roomId).Select(x => x.IsArchived).SingleAsync());
        }

        await catalog.SetArchivedAsync(new Subroom { Id = subroomId }, false);
        await using var restoredDb = CreateContext();
        Assert.False(await restoredDb.Subrooms.Where(x => x.Id == subroomId).Select(x => x.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task DeleteArchivedTransition_ClearsInboundResolverIdAndPreservesAuthoredText()
    {
        Guid targetTransitionId;
        Guid sourceTransitionId;
        await using (var db = CreateContext())
        {
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
            db.Rooms.AddRange(sourceRoom, targetRoom);
            await db.SaveChangesAsync();
            var targetTransition = new RoomTransition { RoomId = targetRoom.Id, Alias = "IN", FriendlyName = "Entrance", SortOrder = 0 };
            var sourceTransition = new RoomTransition { RoomId = sourceRoom.Id, Alias = "OUT", FriendlyName = "Exit", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "IN", SortOrder = 0 };
            db.RoomTransitions.AddRange(targetTransition, sourceTransition);
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            targetTransition.IsArchived = true;
            await db.SaveChangesAsync();
            targetTransitionId = targetTransition.Id;
            sourceTransitionId = sourceTransition.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await catalog.DeleteTransitionPermanentlyAsync(targetTransitionId);

        await using var verificationDb = CreateContext();
        var persistedSourceTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == sourceTransitionId);
        Assert.Equal("IN", persistedSourceTransition.DestinationTransitionAliasText);
        Assert.Null(persistedSourceTransition.ResolvedDestinationTransitionId);
    }

    [Fact]
    public async Task ResolutionReport_ExposesAmbiguousOutOfSyncAndArchivedStates()
    {
        await using var db = CreateContext();
        var source = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var destination = new Room { FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
        db.Rooms.AddRange(source, destination);
        await db.SaveChangesAsync();
        var first = new RoomTransition { RoomId = destination.Id, Alias = "IN", FriendlyName = "First", SortOrder = 0 };
        var second = new RoomTransition { RoomId = destination.Id, Alias = "IN", FriendlyName = "Second", SortOrder = 1 };
        var sourceTransition = new RoomTransition { RoomId = source.Id, Alias = "OUT", FriendlyName = "Exit", DestinationRoomReferenceText = "destination", DestinationTransitionAliasText = "IN", SortOrder = 0, ResolvedDestinationRoomId = source.Id };
        db.RoomTransitions.AddRange(first, second, sourceTransition);
        await db.SaveChangesAsync();

        var report = await new LogicReferenceResolver(db).GetResolutionReportAsync();
        Assert.Contains(report.References, x => x.EntityId == sourceTransition.Id && x.FieldName == nameof(RoomTransition.DestinationRoomReferenceText) && x.Status == ReferenceResolutionStatus.OutOfSync);
        Assert.Contains(report.References, x => x.EntityId == sourceTransition.Id && x.FieldName == nameof(RoomTransition.DestinationTransitionAliasText) && x.Status == ReferenceResolutionStatus.Ambiguous);

        destination.IsArchived = true;
        await db.SaveChangesAsync();
        report = await new LogicReferenceResolver(db).GetResolutionReportAsync();
        Assert.Contains(report.References, x => x.EntityId == sourceTransition.Id && x.FieldName == nameof(RoomTransition.DestinationRoomReferenceText) && x.Status == ReferenceResolutionStatus.TargetArchived);
    }

    [Fact]
    public async Task CatalogSave_RejectsResolvedObjectsOwnedByAnotherRoom()
    {
        RoomTransition transition;
        await using (var db = CreateContext())
        {
            var owningRoom = new Room { FriendlyName = "Owner", ReferenceId = "owner", SortOrder = 0 };
            var otherRoom = new Room { FriendlyName = "Other", ReferenceId = "other", SortOrder = 1 };
            db.Rooms.AddRange(owningRoom, otherRoom);
            await db.SaveChangesAsync();
            var foreignSubroom = new Subroom { RoomId = otherRoom.Id, FriendlyName = "Foreign", ReferenceId = "foreign", SortOrder = 0 };
            db.Subrooms.Add(foreignSubroom);
            await db.SaveChangesAsync();
            transition = new RoomTransition { RoomId = owningRoom.Id, Alias = "A", FriendlyName = "Transition", ResolvedSourceSubroomId = foreignSubroom.Id, SortOrder = 0 };
            db.RoomTransitions.Add(transition);
            await db.SaveChangesAsync();
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveAsync(transition));
    }

    [Fact]
    public async Task DuplicateAliasesAndConnections_PersistWithoutRejection()
    {
        await using var db = CreateContext();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        db.RoomTransitions.AddRange(
            new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "First", SortOrder = 0 },
            new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "Second", SortOrder = 1 });
        db.SubroomConnections.AddRange(
            new SubroomConnection { RoomId = room.Id, Alias = "C", FriendlyName = "First", SourceSubroomReferenceText = "one", DestinationSubroomReferenceText = "two", SortOrder = 0 },
            new SubroomConnection { RoomId = room.Id, Alias = "C", FriendlyName = "Second", SourceSubroomReferenceText = "one", DestinationSubroomReferenceText = "two", SortOrder = 1 });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RoomTransitions.CountAsync(x => x.Alias == "A"));
        Assert.Equal(2, await db.SubroomConnections.CountAsync(x => x.Alias == "C"));
    }

    [Fact]
    public async Task AuthoredDataViolations_PersistAfterMigration()
    {
        await using var db = CreateContext();
        var firstRoom = new Room { FriendlyName = "First room", ReferenceId = "duplicate-room", InGameId = "scene-1", SortOrder = 0 };
        var secondRoom = new Room { FriendlyName = "Second room", ReferenceId = "duplicate-room", InGameId = "scene-1", SortOrder = 1 };
        db.Rooms.AddRange(firstRoom, secondRoom);
        await db.SaveChangesAsync();

        db.Subrooms.AddRange(
            new Subroom { RoomId = firstRoom.Id, FriendlyName = "Duplicate subroom", ReferenceId = "duplicate-subroom", SortOrder = 0 },
            new Subroom { RoomId = firstRoom.Id, FriendlyName = "Duplicate subroom", ReferenceId = "duplicate-subroom", SortOrder = 1 });
        db.RoomTransitions.AddRange(
            new RoomTransition { RoomId = firstRoom.Id, Alias = "LONG", FriendlyName = "Duplicate transition", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "IN", SortOrder = 0 },
            new RoomTransition { RoomId = firstRoom.Id, Alias = "LONG", FriendlyName = "Duplicate transition", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "IN", SortOrder = 1 },
            new RoomTransition { RoomId = firstRoom.Id, Alias = string.Empty, FriendlyName = "Blank alias", SortOrder = 2 });
        db.SubroomConnections.AddRange(
            new SubroomConnection { RoomId = firstRoom.Id, Alias = "LONG", FriendlyName = "Duplicate connection", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", SortOrder = 0 },
            new SubroomConnection { RoomId = firstRoom.Id, Alias = "LONG", FriendlyName = "Duplicate connection", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", SortOrder = 1 },
            new SubroomConnection { RoomId = firstRoom.Id, Alias = string.Empty, FriendlyName = "Blank alias", SortOrder = 2 });
        db.CheckLocations.AddRange(
            new CheckLocation { RoomId = firstRoom.Id, FriendlyName = "Duplicate check", SortOrder = 0 },
            new CheckLocation { RoomId = firstRoom.Id, FriendlyName = "Duplicate check", SortOrder = 1 });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Rooms.CountAsync(x => x.ReferenceId == "duplicate-room" && x.InGameId == "scene-1"));
        Assert.Equal(2, await db.Subrooms.CountAsync(x => x.RoomId == firstRoom.Id && x.ReferenceId == "duplicate-subroom" && x.FriendlyName == "Duplicate subroom"));
        Assert.Equal(2, await db.RoomTransitions.CountAsync(x => x.RoomId == firstRoom.Id && x.Alias == "LONG" && x.FriendlyName == "Duplicate transition"));
        Assert.Equal(1, await db.RoomTransitions.CountAsync(x => x.RoomId == firstRoom.Id && x.Alias == string.Empty));
        Assert.Equal(2, await db.SubroomConnections.CountAsync(x => x.RoomId == firstRoom.Id && x.Alias == "LONG" && x.FriendlyName == "Duplicate connection"));
        Assert.Equal(1, await db.SubroomConnections.CountAsync(x => x.RoomId == firstRoom.Id && x.Alias == string.Empty));
        Assert.Equal(2, await db.CheckLocations.CountAsync(x => x.RoomId == firstRoom.Id && x.FriendlyName == "Duplicate check"));
    }

    [Fact]
    public async Task ImportedMetadata_PersistsForTransitionsAndChecks()
    {
        await using var db = CreateContext();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        var transition = new RoomTransition
        {
            RoomId = room.Id,
            Alias = "OUT",
            FriendlyName = "Exit",
            InGameId = "transition-id",
            InGamePositionX = 1.25,
            InGamePositionY = 2.5,
            InGamePositionZ = 3.75,
            AnnotationSceneUnitX = 13.25,
            AnnotationSceneUnitY = 14.5,
            LocalPositionX = 4.25,
            LocalPositionY = 5.5,
            LocalPositionZ = 6.75,
            SortOrder = 0
        };
        var check = new CheckLocation
        {
            RoomId = room.Id,
            FriendlyName = "Check",
            InGameId = "check-id",
            InGamePositionX = 7.25,
            InGamePositionY = 8.5,
            InGamePositionZ = 9.75,
            AnnotationSceneUnitX = 19.25,
            AnnotationSceneUnitY = 20.5,
            LocalPositionX = 10.25,
            LocalPositionY = 11.5,
            LocalPositionZ = 12.75,
            SortOrder = 0
        };
        db.AddRange(transition, check);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var persistedTransition = await db.RoomTransitions.SingleAsync();
        var persistedCheck = await db.CheckLocations.SingleAsync();

        Assert.Equal("transition-id", persistedTransition.InGameId);
        Assert.Equal(1.25, persistedTransition.InGamePositionX);
        Assert.Equal(2.5, persistedTransition.InGamePositionY);
        Assert.Equal(3.75, persistedTransition.InGamePositionZ);
        Assert.Equal(13.25, persistedTransition.AnnotationSceneUnitX);
        Assert.Equal(14.5, persistedTransition.AnnotationSceneUnitY);
        Assert.Equal(4.25, persistedTransition.LocalPositionX);
        Assert.Equal(5.5, persistedTransition.LocalPositionY);
        Assert.Equal(6.75, persistedTransition.LocalPositionZ);
        Assert.Equal("check-id", persistedCheck.InGameId);
        Assert.Equal(7.25, persistedCheck.InGamePositionX);
        Assert.Equal(8.5, persistedCheck.InGamePositionY);
        Assert.Equal(9.75, persistedCheck.InGamePositionZ);
        Assert.Equal(19.25, persistedCheck.AnnotationSceneUnitX);
        Assert.Equal(20.5, persistedCheck.AnnotationSceneUnitY);
        Assert.Equal(10.25, persistedCheck.LocalPositionX);
        Assert.Equal(11.5, persistedCheck.LocalPositionY);
        Assert.Equal(12.75, persistedCheck.LocalPositionZ);
    }

    [Fact]
    public async Task NewVerificationAndApworldDefaults_PersistAsUnknownAndIncluded()
    {
        await using var db = CreateContext();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        db.AddRange(
            new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "Transition", SortOrder = 0 },
            new SubroomConnection { RoomId = room.Id, Alias = "B", FriendlyName = "Connection", SourceSubroomReferenceText = string.Empty, DestinationSubroomReferenceText = string.Empty, SortOrder = 0 },
            new CheckLocation { RoomId = room.Id, FriendlyName = "Check", SortOrder = 0 });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.Null((await db.RoomTransitions.SingleAsync()).IsVerified);
        Assert.Null((await db.SubroomConnections.SingleAsync()).IsVerified);
        Assert.Null((await db.CheckLocations.SingleAsync()).IsVerified);
        Assert.True((await db.CheckLocations.SingleAsync()).IsIncludedInApworld);
    }

    [Fact]
    public async Task VerificationMigration_ConvertsLegacyValuesToUnknown()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"silksong-logic-legacy-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={legacyPath}").Options;
        var roomId = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var checkId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        try
        {
            await using var db = new LogicDbContext(options);
            await db.Database.MigrateAsync("20260805012637_AddImportedRecordMetadata");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Rooms" ("Id", "RoomGroupId", "ReferenceId", "FriendlyName", "InGameId", "Comments", "SortOrder", "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc")
                VALUES ({roomId}, NULL, {"room"}, {"Room"}, NULL, {""}, 0, 0, NULL, {now}, {now});
                INSERT INTO "RoomTransitions" ("Id", "RoomId", "Alias", "FriendlyName", "SourceSubroomReferenceText", "DestinationRoomReferenceText", "DestinationTransitionAliasText", "Requirements", "Notes", "ResolvedSourceSubroomId", "ResolvedDestinationRoomId", "ResolvedDestinationTransitionId", "SortOrder", "IsTodo", "NeedsVerification", "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc", "InGameId", "WorldPositionX", "WorldPositionY", "WorldPositionZ", "LocalPositionX", "LocalPositionY", "LocalPositionZ")
                VALUES ({transitionId}, {roomId}, {"A"}, {"Transition"}, NULL, NULL, NULL, {""}, {""}, NULL, NULL, NULL, 0, 0, 1, 0, NULL, {now}, {now}, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                INSERT INTO "SubroomConnections" ("Id", "RoomId", "Alias", "FriendlyName", "SourceSubroomReferenceText", "DestinationSubroomReferenceText", "Requirements", "Notes", "ResolvedSourceSubroomId", "ResolvedDestinationSubroomId", "SortOrder", "IsTodo", "NeedsVerification", "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc")
                VALUES ({connectionId}, {roomId}, {"B"}, {"Connection"}, {""}, {""}, {""}, {""}, NULL, NULL, 0, 0, 1, 0, NULL, {now}, {now});
                INSERT INTO "CheckLocations" ("Id", "RoomId", "FriendlyName", "SubroomReferenceText", "Requirements", "Notes", "IsIncludedInApworld", "ApworldLocationReferenceText", "ResolvedSubroomId", "SortOrder", "IsTodo", "NeedsVerification", "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc", "InGameId", "WorldPositionX", "WorldPositionY", "WorldPositionZ", "LocalPositionX", "LocalPositionY", "LocalPositionZ")
                VALUES ({checkId}, {roomId}, {"Check"}, NULL, {""}, {""}, 0, NULL, NULL, 0, 0, 1, 0, NULL, {now}, {now}, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                """);

            await db.Database.MigrateAsync();
            Assert.Null((await db.RoomTransitions.SingleAsync(x => x.Id == transitionId)).IsVerified);
            Assert.Null((await db.SubroomConnections.SingleAsync(x => x.Id == connectionId)).IsVerified);
            Assert.Null((await db.CheckLocations.SingleAsync(x => x.Id == checkId)).IsVerified);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('CheckLocations') WHERE name = 'ApworldLocationReferenceText';";
            if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
            await using var reader = await command.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
        }
    }

    [Fact]
    public async Task VerificationDefaultMigration_PreservesExistingVerificationValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-logic-verification-default-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options;

        try
        {
            await using (var db = new LogicDbContext(options))
            {
                await db.Database.MigrateAsync("20260815170557_AddTransitionAnnotationVisibility");
                var room = new Room { Id = Guid.NewGuid(), FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
                var now = DateTime.UtcNow;
                await db.Database.ExecuteSqlAsync($"INSERT INTO Rooms (Id, FriendlyName, ReferenceId, SortOrder, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({room.Id}, {room.FriendlyName}, {room.ReferenceId}, {room.SortOrder}, 0, {now}, {now});");
                db.AddRange(
                    new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "Transition", SortOrder = 0, IsVerified = true },
                    new SubroomConnection { RoomId = room.Id, Alias = "B", FriendlyName = "Connection", SourceSubroomReferenceText = string.Empty, DestinationSubroomReferenceText = string.Empty, SortOrder = 0, IsVerified = false },
                    new CheckLocation { RoomId = room.Id, FriendlyName = "Check", SortOrder = 0, IsVerified = true });
                await db.SaveChangesAsync();
                await db.Database.MigrateAsync();
            }

            await using var verification = new LogicDbContext(options);
            Assert.True((await verification.RoomTransitions.SingleAsync()).IsVerified);
            Assert.False((await verification.SubroomConnections.SingleAsync()).IsVerified);
            Assert.True((await verification.CheckLocations.SingleAsync()).IsVerified);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SceneImport_CreatesRoomRecordsMetadataAndUniqueDestinationReferences()
    {
        Guid sourceGroupId;
        await using (var db = CreateContext())
        {
            var destination = new Room { FriendlyName = "Destination", ReferenceId = "destination", InGameId = "DestinationScene", SortOrder = 0 };
            db.Rooms.Add(destination);
            await db.SaveChangesAsync();
            db.RoomTransitions.Add(new RoomTransition { RoomId = destination.Id, Alias = "IN", FriendlyName = "Entry", InGameId = "entry-point", SortOrder = 0 });
            var map = new Map { InGameId = "Map", SortOrder = 0 };
            db.Maps.Add(map);
            await db.SaveChangesAsync();
            var zone = new MapZone { MapId = map.Id, InGameId = "Source zone" };
            var group = new RoomGroup { FriendlyName = "Source group", ZoneReferenceText = "source zone", SortOrder = 0 };
            db.AddRange(zone, group);
            await db.SaveChangesAsync();
            db.MapScenes.Add(new MapScene { MapZoneId = zone.Id, InGameId = "Source Scene" });
            await db.SaveChangesAsync();
            sourceGroupId = group.Id;
        }

        var exit = new SceneDumpObject(
            "source-exit", null, null, true, true,
            new ScenePosition(1, 2, 3), new ScenePosition(4, 5, 6),
            [new SceneDumpComponent("TransitionPoint", null, " DestinationScene ", " ENTRY-POINT ")], []);
        exit.Classification = SceneDumpClassification.Exit;
        var check = new SceneDumpObject(
            "source-check", null, null, true, true,
            new ScenePosition(7, 8, 9), null,
            [new SceneDumpComponent("PersistentItem", "check-id", null, null)], []);
        check.Classification = SceneDumpClassification.Check;
        var review = new SceneDumpReview("Source Scene", [exit, check], []);

        var result = await new SceneImportService(new TestDbContextFactory(databasePath)).ImportAsync(review, null, new HashSet<Guid> { exit.Id, check.Id });

        await using var verificationDb = CreateContext();
        var room = await verificationDb.Rooms.SingleAsync(x => x.Id == result.RoomId);
        var transition = await verificationDb.RoomTransitions.SingleAsync(x => x.RoomId == room.Id);
        var persistedCheck = await verificationDb.CheckLocations.SingleAsync(x => x.RoomId == room.Id);
        Assert.Equal("Source Scene", room.FriendlyName);
        Assert.Equal("source-scene", room.ReferenceId);
        Assert.Equal(sourceGroupId, room.RoomGroupId);
        Assert.Equal("source-exit", transition.InGameId);
        Assert.Equal(1, transition.InGamePositionX);
        Assert.Equal((1d, 2d), (transition.AnnotationSceneUnitX, transition.AnnotationSceneUnitY));
        Assert.True(transition.EnableAnnotation);
        Assert.Equal(6, transition.LocalPositionZ);
        Assert.Equal("destination", transition.DestinationRoomReferenceText);
        Assert.Equal("IN", transition.DestinationTransitionAliasText);
        Assert.Equal("check-id", persistedCheck.InGameId);
        Assert.True(persistedCheck.IsIncludedInApworld);
        Assert.Equal(8, persistedCheck.InGamePositionY);
        Assert.Equal((7d, 8d), (persistedCheck.AnnotationSceneUnitX, persistedCheck.AnnotationSceneUnitY));
        Assert.True(persistedCheck.EnableAnnotation);
    }

    [Fact]
    public async Task SceneImport_UpdatesBlankExitGameIdWithFriendlyNameCreateAtomically()
    {
        Guid roomId;
        Guid transitionId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", InGameId = "Scene", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var transition = new RoomTransition
            {
                RoomId = room.Id, Alias = "OUT", FriendlyName = "Authored exit", InGamePositionX = 1,
                AnnotationSceneUnitX = 50, AnnotationSceneUnitY = 60, SourceSubroomReferenceText = "source", DestinationRoomReferenceText = "destination",
                DestinationTransitionAliasText = "IN", Requirements = "dash", Notes = "authored", IsTodo = true, IsVerified = true, SortOrder = 0
            };
            db.RoomTransitions.Add(transition);
            await db.SaveChangesAsync();
            roomId = room.Id;
            transitionId = transition.Id;
        }

        var exit = new SceneDumpObject("new-exit", null, null, null, null, new ScenePosition(7, 8, 9), new ScenePosition(1, 2, 3), [new SceneDumpComponent("TransitionPoint", null, null, null)], []);
        exit.Classification = SceneDumpClassification.Exit;
        var check = new SceneDumpObject("Check object", null, null, null, null, new ScenePosition(4, 5, 6), null, [new SceneDumpComponent("PersistentItem", "new-check", null, null)], []);
        check.Classification = SceneDumpClassification.Check;

        await new SceneImportService(new TestDbContextFactory(databasePath)).ImportAsync(
            new SceneDumpReview("Scene", [exit, check], []), roomId,
            [new SceneImportInstruction(exit.Id, SceneImportMode.Update, transitionId, "ignored"), new SceneImportInstruction(check.Id, SceneImportMode.Create, null, "Chosen check name")]);

        await using var verificationDb = CreateContext();
        var updated = await verificationDb.RoomTransitions.SingleAsync(item => item.Id == transitionId);
        var created = await verificationDb.CheckLocations.SingleAsync();
        Assert.Equal("new-exit", updated.InGameId);
        Assert.Equal(7, updated.InGamePositionX);
        Assert.Equal(3, updated.LocalPositionZ);
        Assert.Equal("Authored exit", updated.FriendlyName);
        Assert.Equal("OUT", updated.Alias);
        Assert.Equal("source", updated.SourceSubroomReferenceText);
        Assert.Equal("destination", updated.DestinationRoomReferenceText);
        Assert.Equal("IN", updated.DestinationTransitionAliasText);
        Assert.Equal("dash", updated.Requirements);
        Assert.Equal("authored", updated.Notes);
        Assert.True(updated.IsTodo);
        Assert.True(updated.IsVerified);
        Assert.Equal(50, updated.AnnotationSceneUnitX);
        Assert.True(updated.EnableAnnotation);
        Assert.Equal("Chosen check name", created.FriendlyName);
        Assert.Equal("new-check", created.InGameId);
        Assert.True(created.IsIncludedInApworld);
        Assert.Equal((4d, 5d), (created.AnnotationSceneUnitX, created.AnnotationSceneUnitY));
        Assert.True(created.EnableAnnotation);
    }

    [Fact]
    public async Task SceneImport_InvalidUpdateTargetRollsBackAllInstructions()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", InGameId = "Scene", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var exit = new SceneDumpObject("Exit", null, null, null, null, null, null, [new SceneDumpComponent("TransitionPoint", null, null, null)], []);
        exit.Classification = SceneDumpClassification.Exit;
        var check = new SceneDumpObject("Check", null, null, null, null, null, null, [new SceneDumpComponent("PersistentItem", "check", null, null)], []);
        check.Classification = SceneDumpClassification.Check;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SceneImportService(new TestDbContextFactory(databasePath)).ImportAsync(new SceneDumpReview("Scene", [exit, check], []), roomId, [new SceneImportInstruction(exit.Id, SceneImportMode.Create, null, "Exit"), new SceneImportInstruction(check.Id, SceneImportMode.Update, Guid.NewGuid(), "Check")]));

        await using var verificationDb = CreateContext();
        Assert.Empty(await verificationDb.RoomTransitions.ToListAsync());
        Assert.Empty(await verificationDb.CheckLocations.ToListAsync());
    }

    [Fact]
    public async Task SceneLayoutFields_PersistWithDisabledAnnotationDefaults()
    {
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 50, SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            db.AddRange(
                new Subroom { RoomId = room.Id, FriendlyName = "Subroom", ReferenceId = "subroom", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4, SortOrder = 0 },
                new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Connection", SceneUnitX = 5, SceneUnitY = 6, SortOrder = 0 },
                new CheckLocation { RoomId = room.Id, FriendlyName = "Check", SortOrder = 0 });
            await db.SaveChangesAsync();
        }

        await using var verificationDb = CreateContext();
        var persistedRoom = await verificationDb.Rooms.SingleAsync();
        var subroom = await verificationDb.Subrooms.SingleAsync();
        var connection = await verificationDb.SubroomConnections.SingleAsync();
        var check = await verificationDb.CheckLocations.SingleAsync();
        Assert.Equal(100, persistedRoom.SceneUnitWidth);
        Assert.Equal(50, persistedRoom.SceneUnitHeight);
        Assert.False(persistedRoom.IsSceneImageStale);
        Assert.Equal(1, subroom.SceneUnitX);
        Assert.Equal(4, subroom.SceneUnitHeight);
        Assert.True(connection.EnableAnnotation);
        Assert.Equal(5, connection.SceneUnitX);
        Assert.True(check.EnableAnnotation);
    }

    [Fact]
    public async Task SceneImageFields_PersistAndChangedCompleteDimensionsMarkCaptureStale()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room
            {
                FriendlyName = "Room",
                ReferenceId = "room",
                SceneUnitWidth = 100,
                SceneUnitHeight = 50,
                SceneImageScaleXPercent = 90,
                SceneImageScaleYPercent = 110,
                SceneImagePanXPercent = 5,
                SceneImagePanYPercent = -5,
                SortOrder = 0
            };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var document = await catalog.GetRoomAsync(roomId);
        document!.Room.SceneUnitWidth = 120;
        await catalog.SaveAsync(document.Room);
        await catalog.SetArchivedAsync(document.Room, true);
        await catalog.SetArchivedAsync(document.Room, false);

        await using var verificationDb = CreateContext();
        var persisted = await verificationDb.Rooms.SingleAsync();
        Assert.Equal(90, persisted.SceneImageScaleXPercent);
        Assert.Equal(110, persisted.SceneImageScaleYPercent);
        Assert.Equal(5, persisted.SceneImagePanXPercent);
        Assert.Equal(-5, persisted.SceneImagePanYPercent);
        Assert.True(persisted.IsSceneImageStale);
        Assert.False(persisted.IsArchived);
    }

    [Fact]
    public async Task SceneImageSave_RejectsIncompleteTransform()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var document = await catalog.GetRoomAsync(roomId);
        document!.Room.SceneImageScaleXPercent = 100;

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveAsync(document.Room));
        await using var verificationDb = CreateContext();
        Assert.Null((await verificationDb.Rooms.SingleAsync()).SceneImageScaleXPercent);
    }

    [Fact]
    public async Task SceneImageCoverageMigration_ConvertsCompleteLegacyMagnificationWithoutChangingPanOrStaleState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-scene-image-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new LogicDbContext(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync("20260807065753_AddRoomSceneImageCapture");
            var room = new Room
            {
                FriendlyName = "Room",
                ReferenceId = "room",
                SceneImageScaleXPercent = 714.2857142857143,
                SceneImageScaleYPercent = 500,
                SceneImagePanXPercent = 36,
                SceneImagePanYPercent = -30,
                IsSceneImageStale = true,
                SortOrder = 0
            };
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlAsync($"INSERT INTO Rooms (Id, FriendlyName, ReferenceId, SceneImageScaleXPercent, SceneImageScaleYPercent, SceneImagePanXPercent, SceneImagePanYPercent, IsSceneImageStale, SortOrder, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({room.Id}, {room.FriendlyName}, {room.ReferenceId}, {room.SceneImageScaleXPercent}, {room.SceneImageScaleYPercent}, {room.SceneImagePanXPercent}, {room.SceneImagePanYPercent}, 1, {room.SortOrder}, 0, {now}, {now});");

            await db.Database.MigrateAsync();

            var migrated = await db.Rooms.AsNoTracking().SingleAsync();
            Assert.Equal(14, migrated.SceneImageScaleXPercent!.Value, 6);
            Assert.Equal(20, migrated.SceneImageScaleYPercent!.Value, 6);
            Assert.Equal(36, migrated.SceneImagePanXPercent);
            Assert.Equal(-30, migrated.SceneImagePanYPercent);
            Assert.True(migrated.IsSceneImageStale);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SceneReadyImport_ReplacementDimensionsMarkExistingCaptureStale()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room
            {
                FriendlyName = "Room",
                ReferenceId = "room",
                InGameId = "Scene",
                SceneUnitWidth = 10,
                SceneUnitHeight = 20,
                SceneImageScaleXPercent = 100,
                SceneImageScaleYPercent = 100,
                SceneImagePanXPercent = 0,
                SceneImagePanYPercent = 0,
                SortOrder = 0
            };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        await new SceneImportService(new TestDbContextFactory(databasePath)).ImportAsync(new SceneDumpReview("Scene", [], [], new SceneUnitSize(30, 40)), roomId, new HashSet<Guid>(), new SceneUnitSize(30, 40));

        await using var verificationDb = CreateContext();
        Assert.True(await verificationDb.Rooms.Select(room => room.IsSceneImageStale).SingleAsync());
    }

    [Fact]
    public async Task DeleteArchivedRoom_RemovesItsGeneratedSceneImage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silksong-room-delete-tests-{Guid.NewGuid():N}");
        try
        {
            Guid roomId;
            await using (var db = CreateContext())
            {
                var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0, IsArchived = true };
                db.Rooms.Add(room);
                await db.SaveChangesAsync();
                roomId = room.Id;
            }

            var files = new SceneImageFileService(root);
            var path = files.GetRoomImagePath(roomId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);

            await new LogicCatalogService(new TestDbContextFactory(databasePath), files).DeleteRoomPermanentlyAsync(roomId);

            Assert.False(File.Exists(path));
            await using var verificationDb = CreateContext();
            Assert.False(await verificationDb.Rooms.AnyAsync(room => room.Id == roomId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SceneReadyImport_FillsOnlyEmptyDimensionsAndAllowsExplicitReplacement()
    {
        Guid emptyRoomId;
        Guid completeRoomId;
        Guid incompleteRoomId;
        await using (var db = CreateContext())
        {
            var empty = new Room { FriendlyName = "Empty", ReferenceId = "empty", InGameId = "EmptyScene", SortOrder = 0 };
            var complete = new Room { FriendlyName = "Complete", ReferenceId = "complete", InGameId = "CompleteScene", SceneUnitWidth = 10, SceneUnitHeight = 20, SortOrder = 1 };
            var incomplete = new Room { FriendlyName = "Incomplete", ReferenceId = "incomplete", InGameId = "IncompleteScene", SceneUnitWidth = 10, SortOrder = 2 };
            db.AddRange(empty, complete, incomplete);
            await db.SaveChangesAsync();
            emptyRoomId = empty.Id;
            completeRoomId = complete.Id;
            incompleteRoomId = incomplete.Id;
        }

        var importer = new SceneImportService(new TestDbContextFactory(databasePath));
        var parsed = new SceneUnitSize(30, 40);
        await importer.ImportAsync(new SceneDumpReview("EmptyScene", [], [], parsed), emptyRoomId, new HashSet<Guid>());
        await importer.ImportAsync(new SceneDumpReview("CompleteScene", [], [], parsed), completeRoomId, new HashSet<Guid>());
        await importer.ImportAsync(new SceneDumpReview("IncompleteScene", [], [], parsed), incompleteRoomId, new HashSet<Guid>());
        await importer.ImportAsync(new SceneDumpReview("CompleteScene", [], [], parsed), completeRoomId, new HashSet<Guid>(), new SceneUnitSize(31, 41));

        await using var verificationDb = CreateContext();
        var persistedEmpty = await verificationDb.Rooms.SingleAsync(room => room.Id == emptyRoomId);
        var persistedComplete = await verificationDb.Rooms.SingleAsync(room => room.Id == completeRoomId);
        var persistedIncomplete = await verificationDb.Rooms.SingleAsync(room => room.Id == incompleteRoomId);
        Assert.Equal(30, persistedEmpty.SceneUnitWidth);
        Assert.Equal(40, persistedEmpty.SceneUnitHeight);
        Assert.Equal(31, persistedComplete.SceneUnitWidth);
        Assert.Equal(41, persistedComplete.SceneUnitHeight);
        Assert.Equal(10, persistedIncomplete.SceneUnitWidth);
        Assert.Null(persistedIncomplete.SceneUnitHeight);
    }

    [Fact]
    public async Task SceneReadyImport_InvalidReplacementRollsBackDimensionAndChildren()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", InGameId = "Scene", SceneUnitWidth = 10, SceneUnitHeight = 20, SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var exit = new SceneDumpObject("Exit", null, null, null, null, null, null, [new SceneDumpComponent("TransitionPoint", null, null, null)], []);
        exit.Classification = SceneDumpClassification.Exit;
        var review = new SceneDumpReview("Scene", [exit], [], new SceneUnitSize(30, 40));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SceneImportService(new TestDbContextFactory(databasePath)).ImportAsync(review, roomId, new HashSet<Guid> { exit.Id }, new SceneUnitSize(0, 40)));

        await using var verificationDb = CreateContext();
        var persistedRoom = await verificationDb.Rooms.SingleAsync();
        Assert.Equal(10, persistedRoom.SceneUnitWidth);
        Assert.Equal(20, persistedRoom.SceneUnitHeight);
        Assert.Empty(await verificationDb.RoomTransitions.ToListAsync());
    }

    [Fact]
    public async Task SceneLayoutMarkerPositions_PersistAsOverridesWithoutReplacingInGameMetadata()
    {
        Guid transitionId;
        Guid checkId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var transition = new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "Exit", InGameId = "exit-id", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, SortOrder = 0 };
            var check = new CheckLocation { RoomId = room.Id, FriendlyName = "Check", InGameId = "check-id", EnableAnnotation = true, InGamePositionX = 4, InGamePositionY = 5, InGamePositionZ = 6, SortOrder = 0 };
            db.AddRange(transition, check);
            await db.SaveChangesAsync();
            transitionId = transition.Id;
            checkId = check.Id;
        }

        await using (var db = CreateContext())
        {
            var transition = await db.RoomTransitions.SingleAsync(item => item.Id == transitionId);
            var check = await db.CheckLocations.SingleAsync(item => item.Id == checkId);
            transition.AnnotationSceneUnitX = 10;
            transition.AnnotationSceneUnitY = 20;
            check.AnnotationSceneUnitX = 30;
            check.AnnotationSceneUnitY = 40;
            await db.SaveChangesAsync();
        }

        await using var verificationDb = CreateContext();
        var persistedTransition = await verificationDb.RoomTransitions.SingleAsync(item => item.Id == transitionId);
        var persistedCheck = await verificationDb.CheckLocations.SingleAsync(item => item.Id == checkId);
        Assert.Equal(1, persistedTransition.InGamePositionX);
        Assert.Equal(2, persistedTransition.InGamePositionY);
        Assert.Equal(3, persistedTransition.InGamePositionZ);
        Assert.Equal(10, persistedTransition.AnnotationSceneUnitX);
        Assert.Equal(20, persistedTransition.AnnotationSceneUnitY);
        Assert.Equal("exit-id", persistedTransition.InGameId);
        Assert.Equal(4, persistedCheck.InGamePositionX);
        Assert.Equal(5, persistedCheck.InGamePositionY);
        Assert.Equal(6, persistedCheck.InGamePositionZ);
        Assert.Equal(30, persistedCheck.AnnotationSceneUnitX);
        Assert.Equal(40, persistedCheck.AnnotationSceneUnitY);
        Assert.Equal("check-id", persistedCheck.InGameId);
    }

    [Fact]
    public async Task SceneLayoutConnectionAndSubroomOperations_SynchronizeAndPersistGeometry()
    {
        Guid firstConnectionId;
        Guid secondConnectionId;
        Guid subroomId;
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 40, SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
            var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var destination = new Subroom { RoomId = room.Id, FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
            db.Subrooms.AddRange(source, destination);
            await db.SaveChangesAsync();
            var first = new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", SceneUnitX = 10, SceneUnitY = 11, SortOrder = 0 };
            var second = new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "destination", DestinationSubroomReferenceText = "source", SceneUnitX = 10, SceneUnitY = 11, SortOrder = 1 };
            db.SubroomConnections.AddRange(first, second);
            await db.SaveChangesAsync();
            firstConnectionId = first.Id;
            secondConnectionId = second.Id;
            subroomId = source.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var enabledRows = await catalog.AddConnectionAnnotationAsync(firstConnectionId);
        var movedRows = await catalog.MoveConnectionAnnotationAsync(roomId, secondConnectionId, 12, 13);
        Assert.Equal(2, enabledRows.Count);
        Assert.Equal(2, movedRows.Count);
        Assert.All(movedRows, row =>
        {
            Assert.True(row.EnableAnnotation);
            Assert.Equal(12, row.SceneUnitX);
            Assert.Equal(13, row.SceneUnitY);
            Assert.NotEqual(default, row.UpdatedUtc);
        });
        await catalog.AddSubroomSceneRectangleAsync(subroomId);
        await catalog.UpdateSubroomSceneRectangleAsync(subroomId, 1, 2, 3, 4);

        await using (var verificationDb = CreateContext())
        {
            var connections = await verificationDb.SubroomConnections.OrderBy(item => item.SortOrder).ToListAsync();
            Assert.All(connections, connection => { Assert.True(connection.EnableAnnotation); Assert.Equal(12, connection.SceneUnitX); Assert.Equal(13, connection.SceneUnitY); });
            var subroom = await verificationDb.Subrooms.SingleAsync(item => item.Id == subroomId);
            Assert.Equal(1, subroom.SceneUnitX);
            Assert.Equal(4, subroom.SceneUnitHeight);
        }

        var removedRows = await catalog.RemoveConnectionAnnotationAsync(firstConnectionId);
        Assert.Equal(2, removedRows.Count);
        Assert.All(removedRows, row => Assert.False(row.EnableAnnotation));
        await catalog.RemoveSubroomSceneRectangleAsync(subroomId);
        await using (var clearedDb = CreateContext())
        {
            Assert.All(await clearedDb.SubroomConnections.ToListAsync(), connection => { Assert.False(connection.EnableAnnotation); Assert.Equal(12, connection.SceneUnitX); Assert.Equal(13, connection.SceneUnitY); });
            var clearedSubroom = await clearedDb.Subrooms.SingleAsync(item => item.Id == subroomId);
            Assert.Null(clearedSubroom.SceneUnitX);
            Assert.Null(clearedSubroom.SceneUnitHeight);
        }

        await catalog.AddConnectionAnnotationAsync(firstConnectionId);
        await using var restoredDb = CreateContext();
        Assert.All(await restoredDb.SubroomConnections.ToListAsync(), connection => { Assert.True(connection.EnableAnnotation); Assert.Equal(12, connection.SceneUnitX); Assert.Equal(13, connection.SceneUnitY); });
    }

    [Fact]
    public async Task ConnectionAliasSave_ReconcilesAnnotationForSoleRenameSplitAndMerge()
    {
        Guid firstId;
        Guid secondId;
        Guid targetId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var destination = new Subroom { RoomId = room.Id, FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
            db.Subrooms.AddRange(source, destination);
            await db.SaveChangesAsync();
            var first = new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2, SortOrder = 0 };
            var second = new SubroomConnection { RoomId = room.Id, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "destination", DestinationSubroomReferenceText = "source", EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2, SortOrder = 1 };
            var target = new SubroomConnection { RoomId = room.Id, Alias = "B", FriendlyName = "Other", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination", EnableAnnotation = true, SceneUnitX = 8, SceneUnitY = 9, SortOrder = 2 };
            db.SubroomConnections.AddRange(first, second, target);
            await db.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
            targetId = target.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await using (var readDb = CreateContext())
        {
            var soleRename = await readDb.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == targetId);
            soleRename.Alias = "D";
            await catalog.SaveAsync(soleRename);
        }

        await using (var soleRenameDb = CreateContext())
        {
            var soleRename = await soleRenameDb.SubroomConnections.SingleAsync(item => item.Id == targetId);
            Assert.True(soleRename.EnableAnnotation);
            Assert.Equal(8, soleRename.SceneUnitX);
            Assert.Equal(9, soleRename.SceneUnitY);
        }

        await using (var readDb = CreateContext())
        {
            var split = await readDb.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == firstId);
            split.Alias = "C";
            await catalog.SaveAsync(split);
        }

        await using (var splitDb = CreateContext())
        {
            var split = await splitDb.SubroomConnections.SingleAsync(item => item.Id == firstId);
            var retained = await splitDb.SubroomConnections.SingleAsync(item => item.Id == secondId);
            Assert.False(split.EnableAnnotation);
            Assert.Null(split.SceneUnitX);
            Assert.True(retained.EnableAnnotation);
            Assert.Equal(1, retained.SceneUnitX);
        }

        await using (var readDb = CreateContext())
        {
            var merged = await readDb.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == secondId);
            merged.Alias = "D";
            await catalog.SaveAsync(merged);
        }

        await using var mergeDb = CreateContext();
        var mergedConnection = await mergeDb.SubroomConnections.SingleAsync(item => item.Id == secondId);
        var targetConnection = await mergeDb.SubroomConnections.SingleAsync(item => item.Id == targetId);
        Assert.True(mergedConnection.EnableAnnotation);
        Assert.Equal(targetConnection.SceneUnitX, mergedConnection.SceneUnitX);
        Assert.Equal(targetConnection.SceneUnitY, mergedConnection.SceneUnitY);
    }

    [Fact]
    public async Task CreateTransitionAndConnection_PersistAuthoredAliases()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var transition = await catalog.CreateTransitionAsync(roomId, "OUT", "Exit");
        var connection = await catalog.CreateConnectionAsync(roomId, "C", "Path");

        Assert.Equal("OUT", transition.Alias);
        Assert.Equal("Exit", transition.FriendlyName);
        Assert.Equal("C", connection.Alias);
        Assert.Equal("Path", connection.FriendlyName);
    }

    [Fact]
    public async Task ScaffoldInverseConnection_PersistsReversedAuthoredValuesAndDefaults()
    {
        Guid connectionId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var source = new Subroom { RoomId = room.Id, FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var destination = new Subroom { RoomId = room.Id, FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
            db.Subrooms.AddRange(source, destination);
            await db.SaveChangesAsync();
            var connection = new SubroomConnection
            {
                RoomId = room.Id,
                Alias = "A",
                FriendlyName = "Path",
                SourceSubroomReferenceText = " source ",
                DestinationSubroomReferenceText = "destination",
                Requirements = "requires dash",
                Notes = "original note",
                IsTodo = true,
                IsVerified = true,
                SortOrder = 4
            };
            db.SubroomConnections.Add(connection);
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            connectionId = connection.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var scaffoldOutcome = await catalog.ScaffoldInverseConnectionWithOutcomeAsync(connectionId);
        var inverse = scaffoldOutcome.Entity;

        Assert.True(scaffoldOutcome.RequiresDocumentRefresh);
        Assert.False(scaffoldOutcome.RequiresSidebarRefresh);
        Assert.True(scaffoldOutcome.ResolverElapsed >= TimeSpan.Zero);

        await using var verificationDb = CreateContext();
        var connections = await verificationDb.SubroomConnections.OrderBy(x => x.SortOrder).ToListAsync();
        var original = Assert.Single(connections, x => x.Id == connectionId);
        var persistedInverse = Assert.Single(connections, x => x.Id == inverse.Id);
        Assert.Equal(4, original.SortOrder);
        Assert.Equal("requires dash", original.Requirements);
        Assert.Equal("original note", original.Notes);
        Assert.True(original.IsTodo);
        Assert.True(original.IsVerified);
        Assert.Equal("A", persistedInverse.Alias);
        Assert.Equal("Path", persistedInverse.FriendlyName);
        Assert.Equal("destination", persistedInverse.SourceSubroomReferenceText);
        Assert.Equal(" source ", persistedInverse.DestinationSubroomReferenceText);
        Assert.Equal(string.Empty, persistedInverse.Requirements);
        Assert.Equal(string.Empty, persistedInverse.Notes);
        Assert.False(persistedInverse.IsTodo);
        Assert.Null(persistedInverse.IsVerified);
        Assert.False(persistedInverse.IsArchived);
        Assert.Equal(5, persistedInverse.SortOrder);
    }

    [Fact]
    public async Task SubroomReferenceSnapshot_UpdatesCapturedActiveFieldsAfterRename()
    {
        Guid subroomId;
        Guid archivedTransitionId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var subroom = new Subroom { RoomId = room.Id, FriendlyName = "Old subroom", ReferenceId = "old-subroom", SortOrder = 0 };
            db.Subrooms.Add(subroom);
            await db.SaveChangesAsync();
            var transition = new RoomTransition { RoomId = room.Id, Alias = "A", FriendlyName = "Active transition", SourceSubroomReferenceText = "old-subroom", SortOrder = 0 };
            var archivedTransition = new RoomTransition { RoomId = room.Id, Alias = "B", FriendlyName = "Archived transition", SourceSubroomReferenceText = "old-subroom", SortOrder = 1, IsArchived = true };
            var connection = new SubroomConnection { RoomId = room.Id, Alias = "C", FriendlyName = "Connection", SourceSubroomReferenceText = "old-subroom", DestinationSubroomReferenceText = "old-subroom", SortOrder = 0 };
            var check = new CheckLocation { RoomId = room.Id, FriendlyName = "Check", SubroomReferenceText = "old-subroom", SortOrder = 0 };
            db.AddRange(transition, archivedTransition, connection, check);
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            subroomId = subroom.Id;
            archivedTransitionId = archivedTransition.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var candidates = await catalog.GetSubroomReferenceUpdateCandidatesAsync(subroomId);
        Assert.Equal(4, candidates.Count);

        Guid roomId;
        await using (var roomDb = CreateContext())
        {
            roomId = await roomDb.Rooms.Select(x => x.Id).SingleAsync();
        }

        var document = await catalog.GetRoomAsync(roomId);
        var renamedSubroom = Assert.Single(document!.Subrooms);
        renamedSubroom.ReferenceId = "new-subroom";
        await catalog.SaveAsync(renamedSubroom);

        await using (var unresolvedDb = CreateContext())
        {
            Assert.All(await unresolvedDb.RoomTransitions.Where(x => !x.IsArchived).ToListAsync(), x => Assert.Null(x.ResolvedSourceSubroomId));
            Assert.All(await unresolvedDb.SubroomConnections.ToListAsync(), x =>
            {
                Assert.Null(x.ResolvedSourceSubroomId);
                Assert.Null(x.ResolvedDestinationSubroomId);
            });
            Assert.Null(await unresolvedDb.CheckLocations.Select(x => x.ResolvedSubroomId).SingleAsync());
        }

        await catalog.UpdateReferenceTextAsync(candidates, "new-subroom");

        await using var verificationDb = CreateContext();
        var verifiedActiveTransition = await verificationDb.RoomTransitions.SingleAsync(x => !x.IsArchived);
        var verifiedArchivedTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == archivedTransitionId);
        var verifiedConnection = await verificationDb.SubroomConnections.SingleAsync();
        var verifiedCheck = await verificationDb.CheckLocations.SingleAsync();
        Assert.Equal("new-subroom", verifiedActiveTransition.SourceSubroomReferenceText);
        Assert.Equal("old-subroom", verifiedArchivedTransition.SourceSubroomReferenceText);
        Assert.Equal("new-subroom", verifiedConnection.SourceSubroomReferenceText);
        Assert.Equal("new-subroom", verifiedConnection.DestinationSubroomReferenceText);
        Assert.Equal("new-subroom", verifiedCheck.SubroomReferenceText);
        Assert.Equal(subroomId, verifiedActiveTransition.ResolvedSourceSubroomId);
        Assert.Equal(subroomId, verifiedConnection.ResolvedSourceSubroomId);
        Assert.Equal(subroomId, verifiedConnection.ResolvedDestinationSubroomId);
        Assert.Equal(subroomId, verifiedCheck.ResolvedSubroomId);
    }

    [Fact]
    public async Task RoomReferenceSnapshot_UpdatesOnlyCapturedActiveDestinationAfterRename()
    {
        Guid sourceRoomId;
        Guid targetRoomId;
        Guid activeTransitionId;
        Guid archivedTransitionId;
        Guid unrelatedTransitionId;
        await using (var db = CreateContext())
        {
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "old-target", SortOrder = 1 };
            var otherRoom = new Room { FriendlyName = "Other", ReferenceId = "other", SortOrder = 2 };
            db.AddRange(sourceRoom, targetRoom, otherRoom);
            await db.SaveChangesAsync();
            var targetEntrance = new RoomTransition { RoomId = targetRoom.Id, Alias = "IN", FriendlyName = "Entrance", SortOrder = 0 };
            var activeTransition = new RoomTransition { RoomId = sourceRoom.Id, Alias = "A", FriendlyName = "Active", DestinationRoomReferenceText = "old-target", DestinationTransitionAliasText = "IN", SortOrder = 0 };
            var archivedTransition = new RoomTransition { RoomId = sourceRoom.Id, Alias = "B", FriendlyName = "Archived", DestinationRoomReferenceText = "old-target", DestinationTransitionAliasText = "IN", SortOrder = 1, IsArchived = true };
            var unrelatedTransition = new RoomTransition { RoomId = sourceRoom.Id, Alias = "C", FriendlyName = "Unrelated", DestinationRoomReferenceText = "other", SortOrder = 2 };
            db.AddRange(targetEntrance, activeTransition, archivedTransition, unrelatedTransition);
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            sourceRoomId = sourceRoom.Id;
            targetRoomId = targetRoom.Id;
            activeTransitionId = activeTransition.Id;
            archivedTransitionId = archivedTransition.Id;
            unrelatedTransitionId = unrelatedTransition.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var candidates = await catalog.GetRoomReferenceUpdateCandidatesAsync(targetRoomId);
        var candidate = Assert.Single(candidates);
        Assert.Equal(activeTransitionId, candidate.EntityId);
        Assert.Equal(nameof(RoomTransition.DestinationRoomReferenceText), candidate.FieldName);

        var document = await catalog.GetRoomAsync(targetRoomId);
        document!.Room.ReferenceId = "new-target";
        await catalog.SaveAsync(document.Room);
        await catalog.UpdateReferenceTextAsync(candidates, "new-target");

        await using var verificationDb = CreateContext();
        var verifiedActiveTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == activeTransitionId);
        var verifiedArchivedTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == archivedTransitionId);
        var verifiedUnrelatedTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == unrelatedTransitionId);
        Assert.Equal("new-target", verifiedActiveTransition.DestinationRoomReferenceText);
        Assert.Equal(targetRoomId, verifiedActiveTransition.ResolvedDestinationRoomId);
        Assert.NotNull(verifiedActiveTransition.ResolvedDestinationTransitionId);
        Assert.Equal("old-target", verifiedArchivedTransition.DestinationRoomReferenceText);
        Assert.Equal("other", verifiedUnrelatedTransition.DestinationRoomReferenceText);
        Assert.Equal(sourceRoomId, verifiedUnrelatedTransition.RoomId);
    }

    [Fact]
    public async Task ForeignKeys_RemainEnforcedAfterMigration()
    {
        await using var db = CreateContext();
        db.Subrooms.Add(new Subroom
        {
            RoomId = Guid.NewGuid(),
            FriendlyName = "Orphaned subroom",
            ReferenceId = "orphaned-subroom",
            SortOrder = 0
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task InverseSetup_AppliesBlankFieldsAtomicallyAndPreservesConflictingPartialText()
    {
        Guid sourceId;
        Guid targetId;
        await using (var db = CreateContext())
        {
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
            db.AddRange(sourceRoom, targetRoom);
            await db.SaveChangesAsync();
            var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "IN", FriendlyName = "Target entry", DestinationRoomReferenceText = "conflicting", SortOrder = 0 };
            var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "OUT", FriendlyName = "Source exit", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "IN", SortOrder = 0 };
            db.AddRange(source, target);
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            sourceId = source.Id;
            targetId = target.Id;
        }

        var service = new TransitionInverseSetupService(new TestDbContextFactory(databasePath));
        var draft = await service.GetDraftAsync(sourceId);
        Assert.NotNull(draft);
        Assert.False(draft.FillDestinationRoomReference);
        Assert.True(draft.FillDestinationTransitionAlias);
        Assert.True(await service.ApplyAsync(sourceId));

        await using var verificationDb = CreateContext();
        var targetTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == targetId);
        Assert.Equal("conflicting", targetTransition.DestinationRoomReferenceText);
        Assert.Equal("OUT", targetTransition.DestinationTransitionAliasText);
        Assert.Null(targetTransition.ResolvedDestinationTransitionId);
    }

    [Fact]
    public async Task InverseSetup_IneligibleOrCancelledDraftMakesNoWrite()
    {
        Guid sourceId;
        Guid targetId;
        DateTime targetUpdatedUtc;
        await using (var db = CreateContext())
        {
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
            db.AddRange(sourceRoom, targetRoom);
            await db.SaveChangesAsync();
            var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "IN", FriendlyName = "Target entry", DestinationRoomReferenceText = "already", DestinationTransitionAliasText = "there", SortOrder = 0 };
            var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "OUT", FriendlyName = "Source exit", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "IN", SortOrder = 0 };
            db.AddRange(source, target);
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            sourceId = source.Id;
            targetId = target.Id;
            targetUpdatedUtc = target.UpdatedUtc;
        }

        var service = new TransitionInverseSetupService(new TestDbContextFactory(databasePath));
        Assert.Null(await service.GetDraftAsync(sourceId));
        Assert.False(await service.ApplyAsync(sourceId));

        await using var verificationDb = CreateContext();
        var targetTransition = await verificationDb.RoomTransitions.SingleAsync(x => x.Id == targetId);
        Assert.Equal("already", targetTransition.DestinationRoomReferenceText);
        Assert.Equal("there", targetTransition.DestinationTransitionAliasText);
        Assert.Equal(targetUpdatedUtc, targetTransition.UpdatedUtc);
    }

    [Fact]
    public async Task OrdinaryTransitionCreate_ResolvesBeforeInverseSetupEligibility()
    {
        Guid sourceRoomId;
        Guid targetRoomId;
        Guid targetTransitionId;
        await using (var db = CreateContext())
        {
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
            db.AddRange(sourceRoom, targetRoom);
            await db.SaveChangesAsync();
            var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "IN", FriendlyName = "Target entry", SortOrder = 0 };
            db.RoomTransitions.Add(target);
            await db.SaveChangesAsync();
            sourceRoomId = sourceRoom.Id;
            targetRoomId = targetRoom.Id;
            targetTransitionId = target.Id;
        }

        var factory = new TestDbContextFactory(databasePath);
        var catalog = new LogicCatalogService(factory);
        var created = await catalog.CreateTransitionAsync(sourceRoomId, new RoomTransition
        {
            Alias = "OUT",
            FriendlyName = "Source exit",
            DestinationRoomReferenceText = " target ",
            DestinationTransitionAliasText = "in"
        });

        Assert.Equal(targetRoomId, created.ResolvedDestinationRoomId);
        Assert.Equal(targetTransitionId, created.ResolvedDestinationTransitionId);
        var draft = await new TransitionInverseSetupService(factory).GetDraftAsync(created.Id);
        Assert.NotNull(draft);
        Assert.True(draft.FillDestinationRoomReference);
        Assert.True(draft.FillDestinationTransitionAlias);
    }

    [Fact]
    public async Task ChildCreation_PersistsCompleteSnapshotsAndScopedResolution()
    {
        Guid roomId;
        Guid destinationRoomId;
        Guid destinationTransitionId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            var destinationRoom = new Room { FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
            db.AddRange(room, destinationRoom);
            await db.SaveChangesAsync();
            var destinationTransition = new RoomTransition { RoomId = destinationRoom.Id, Alias = "IN", FriendlyName = "Entrance", SortOrder = 0 };
            db.RoomTransitions.Add(destinationTransition);
            await db.SaveChangesAsync();
            roomId = room.Id;
            destinationRoomId = destinationRoom.Id;
            destinationTransitionId = destinationTransition.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var subroom = await catalog.CreateSubroomWithOutcomeAsync(roomId, new Subroom { FriendlyName = string.Empty, ReferenceId = "inside", Notes = "subroom note" });
        var transition = await catalog.CreateTransitionWithOutcomeAsync(roomId, new RoomTransition { Alias = string.Empty, FriendlyName = "Exit", SourceSubroomReferenceText = "inside", DestinationRoomReferenceText = " destination ", DestinationTransitionAliasText = " in ", Requirements = "requires dash", Notes = "transition note", IsTodo = true });
        var connection = await catalog.CreateConnectionWithOutcomeAsync(roomId, new SubroomConnection { Alias = string.Empty, FriendlyName = string.Empty, SourceSubroomReferenceText = "inside", DestinationSubroomReferenceText = "inside", Requirements = "requires dash", Notes = "connection note", IsTodo = true });
        var check = await catalog.CreateCheckWithOutcomeAsync(roomId, new CheckLocation { FriendlyName = string.Empty, SubroomReferenceText = "inside", Requirements = "requires dash", Notes = "check note", IsIncludedInApworld = false, IsTodo = true });

        Assert.Equal(subroom.Entity.Id, transition.Entity.ResolvedSourceSubroomId);
        Assert.Equal(destinationRoomId, transition.Entity.ResolvedDestinationRoomId);
        Assert.Equal(destinationTransitionId, transition.Entity.ResolvedDestinationTransitionId);
        Assert.Equal(subroom.Entity.Id, connection.Entity.ResolvedSourceSubroomId);
        Assert.Equal(subroom.Entity.Id, connection.Entity.ResolvedDestinationSubroomId);
        Assert.Equal(subroom.Entity.Id, check.Entity.ResolvedSubroomId);
        Assert.True(transition.RequiresSidebarRefresh);
        Assert.True(connection.RequiresSidebarRefresh);
        Assert.True(check.RequiresSidebarRefresh);

        // This is the durable half of the shared child-row lifecycle regression:
        // a complete immutable create is followed by one latest-row save for every
        // typed table. The UI lifecycle test proves the client row queues that one
        // follow-up request while it morphs in place.
        var connectionSaves = new ConnectionRoomSaveCoordinator(catalog);
        connectionSaves.RegisterRows([connection.Entity]);
        subroom.Entity.Notes = "post-create subroom note";
        transition.Entity.DestinationTransitionAliasText = "post-create alias";
        connection.Entity.Requirements = "post-create connection requirement";
        check.Entity.IsIncludedInApworld = true;
        await catalog.SaveWithOutcomeAsync(subroom.Entity);
        await catalog.SaveTransitionAliasWithPatchAsync(transition.Entity, roomId);
        await connectionSaves.SaveAsync(connection.Entity, roomId);
        await catalog.SaveWithOutcomeAsync(check.Entity);

        await using var verification = CreateContext();
        Assert.Equal("post-create subroom note", (await verification.Subrooms.SingleAsync(item => item.Id == subroom.Entity.Id)).Notes);
        var persistedTransition = await verification.RoomTransitions.SingleAsync(item => item.Id == transition.Entity.Id);
        Assert.Equal(string.Empty, persistedTransition.Alias);
        Assert.Equal("post-create alias", persistedTransition.DestinationTransitionAliasText);
        Assert.Equal("requires dash", persistedTransition.Requirements);
        Assert.Equal("transition note", persistedTransition.Notes);
        Assert.Null(persistedTransition.IsVerified);
        var persistedConnection = await verification.SubroomConnections.SingleAsync(item => item.Id == connection.Entity.Id);
        Assert.Equal(string.Empty, persistedConnection.Alias);
        Assert.Equal(string.Empty, persistedConnection.FriendlyName);
        Assert.Equal("post-create connection requirement", persistedConnection.Requirements);
        Assert.Equal("connection note", persistedConnection.Notes);
        Assert.Null(persistedConnection.IsVerified);
        var persistedCheck = await verification.CheckLocations.SingleAsync(item => item.Id == check.Entity.Id);
        Assert.Equal(string.Empty, persistedCheck.FriendlyName);
        Assert.True(persistedCheck.IsIncludedInApworld);
        Assert.Null(persistedCheck.IsVerified);
    }

    [Fact]
    public async Task BlankRequirements_PersistVerbatimForAllActiveLogicRowTypes()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var transition = await catalog.CreateTransitionWithOutcomeAsync(roomId, new RoomTransition { Alias = "OUT", FriendlyName = "Exit", Requirements = string.Empty });
        var connection = await catalog.CreateConnectionWithOutcomeAsync(roomId, new SubroomConnection { Alias = "C", FriendlyName = "Path", Requirements = " \t " });
        var check = await catalog.CreateCheckWithOutcomeAsync(roomId, new CheckLocation { FriendlyName = "Check", Requirements = string.Empty });

        await using var verification = CreateContext();
        Assert.Equal(string.Empty, (await verification.RoomTransitions.SingleAsync(item => item.Id == transition.Entity.Id)).Requirements);
        Assert.Equal(" \t ", (await verification.SubroomConnections.SingleAsync(item => item.Id == connection.Entity.Id)).Requirements);
        Assert.Equal(string.Empty, (await verification.CheckLocations.SingleAsync(item => item.Id == check.Entity.Id)).Requirements);
    }

    [Fact]
    public async Task ChildCreation_FailedSnapshotDoesNotCreateDuplicateAndLaterRetryPersistsOnce()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var snapshot = new CheckLocation
        {
            FriendlyName = "Queued control-only check",
            IsIncludedInApworld = false,
            IsTodo = true,
            IsVerified = null
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => catalog.CreateCheckWithOutcomeAsync(Guid.NewGuid(), snapshot));
        await using (var afterFailure = CreateContext())
        Assert.Empty(await afterFailure.CheckLocations.ToListAsync());

        var created = await catalog.CreateCheckWithOutcomeAsync(roomId, snapshot);
        await using var verification = CreateContext();
        var persisted = await verification.CheckLocations.SingleAsync();
        Assert.Equal(created.Entity.Id, persisted.Id);
        Assert.Equal("Queued control-only check", persisted.FriendlyName);
        Assert.False(persisted.IsIncludedInApworld);
        Assert.True(persisted.IsTodo);
        Assert.Null(persisted.IsVerified);
    }

    [Fact]
    public async Task PlaceholderCreation_ReturnsImpactAndResolvesNewRoomReferences()
    {
        Guid sourceRoomId;
        Guid transitionId;
        await using (var db = CreateContext())
        {
            var source = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            db.Rooms.Add(source);
            await db.SaveChangesAsync();
            var transition = new RoomTransition { RoomId = source.Id, Alias = "OUT", FriendlyName = "Exit", DestinationRoomReferenceText = "new-room", SortOrder = 0 };
            db.RoomTransitions.Add(transition);
            await db.SaveChangesAsync();
            sourceRoomId = source.Id;
            transitionId = transition.Id;
        }

        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        var groupOutcome = await catalog.CreateRoomGroupWithOutcomeAsync("New group");
        var roomOutcome = await catalog.CreateRoomWithOutcomeAsync("New room", groupOutcome.Entity.Id);

        Assert.True(groupOutcome.RequiresSidebarRefresh);
        Assert.False(groupOutcome.RequiresDocumentRefresh);
        Assert.True(roomOutcome.RequiresSidebarRefresh);
        Assert.False(roomOutcome.RequiresDocumentRefresh);
        Assert.True(roomOutcome.ResolverElapsed >= TimeSpan.Zero);

        await using var verification = CreateContext();
        var persistedGroup = await verification.RoomGroups.SingleAsync(item => item.Id == groupOutcome.Entity.Id);
        var persistedRoom = await verification.Rooms.SingleAsync(item => item.Id == roomOutcome.Entity.Id);
        var persistedTransition = await verification.RoomTransitions.SingleAsync(item => item.Id == transitionId);
        Assert.Equal("New group", persistedGroup.FriendlyName);
        Assert.Equal("New room", persistedRoom.FriendlyName);
        Assert.Equal(groupOutcome.Entity.Id, persistedRoom.RoomGroupId);
        Assert.Equal(roomOutcome.Entity.Id, persistedTransition.ResolvedDestinationRoomId);
        Assert.Equal(sourceRoomId, persistedTransition.RoomId);
    }

    [Fact]
    public async Task OrdinaryTransitionCreate_WithUnresolvedDestinationDoesNotQualifyForInverseSetup()
    {
        Guid sourceRoomId;
        await using (var db = CreateContext())
        {
            var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
            db.Rooms.Add(sourceRoom);
            await db.SaveChangesAsync();
            sourceRoomId = sourceRoom.Id;
        }

        var factory = new TestDbContextFactory(databasePath);
        var created = await new LogicCatalogService(factory).CreateTransitionAsync(sourceRoomId, new RoomTransition
        {
            Alias = "OUT",
            FriendlyName = "Source exit",
            DestinationRoomReferenceText = "missing",
            DestinationTransitionAliasText = "IN"
        });

        Assert.Null(created.ResolvedDestinationRoomId);
        Assert.Null(created.ResolvedDestinationTransitionId);
        Assert.Null(await new TransitionInverseSetupService(factory).GetDraftAsync(created.Id));
    }

    private LogicDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LogicDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new LogicDbContext(options);
    }

    private sealed class TestDbContextFactory(string path) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();

        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());

        private LogicDbContext Create()
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            return new LogicDbContext(options);
        }
    }
}
