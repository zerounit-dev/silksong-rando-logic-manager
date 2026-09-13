using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite proof for V2 room lifecycle ownership and cleanup.</summary>
public sealed class RoomEditorV2RoomLifecycleCommandTests
{
    [Fact]
    public async Task ArchiveRestore_IsolatesRoomRetainsChildrenAndGeneratedImage_AndGuardsMissing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"silksong-v2-room-image-{Guid.NewGuid():N}");
        try
        {
            var room = new Room { FriendlyName = "Room", ReferenceId = "room" };
            var child = new Subroom { RoomId = room.Id, FriendlyName = "Child", ReferenceId = "child" };
            await using (var db = fixture.CreateDbContext()) { db.AddRange(room, child); await db.SaveChangesAsync(); }
            var files = new SceneImageFileService(root); Directory.CreateDirectory(Path.GetDirectoryName(files.GetRoomImagePath(room.Id))!); await File.WriteAllTextAsync(files.GetRoomImagePath(room.Id), "image");
            var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture);

            Assert.Equal(V2RoomLifecycleCommandStatus.Committed, (await commands.SetRoomArchiveAsync(room.Id, true)).Status);
            await using (var archived = fixture.CreateDbContext())
            {
                var persisted = await archived.Rooms.SingleAsync(x => x.Id == room.Id);
                Assert.True(persisted.IsArchived); Assert.NotNull(persisted.ArchivedUtc);
                Assert.False((await archived.Subrooms.SingleAsync(x => x.Id == child.Id)).IsArchived);
            }
            Assert.True(files.Exists(room.Id));
            Assert.Equal(V2RoomLifecycleCommandStatus.Committed, (await commands.SetRoomArchiveAsync(room.Id, false)).Status);
            await using (var restored = fixture.CreateDbContext()) { var persisted = await restored.Rooms.SingleAsync(x => x.Id == room.Id); Assert.False(persisted.IsArchived); Assert.Null(persisted.ArchivedUtc); }
            Assert.Equal(V2RoomLifecycleCommandStatus.ExpectedFailure, (await commands.DeleteRoomAsync(room.Id)).Status);
            Assert.Equal(V2RoomLifecycleCommandStatus.ExpectedFailure, (await commands.SetRoomArchiveAsync(Guid.NewGuid(), true)).Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArchivedPermanentDelete_RemovesEveryOwnedPartitionClearsInboundResolverIdsPreservesTextAndDeletesImage()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"silksong-v2-room-delete-{Guid.NewGuid():N}");
        try
        {
            var target = new Room { FriendlyName = "Target", ReferenceId = "target", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
            var external = new Room { FriendlyName = "External", ReferenceId = "external" };
            var activeTransition = new RoomTransition { RoomId = target.Id, Alias = "a", FriendlyName = "active" };
            var archivedTransition = new RoomTransition { RoomId = target.Id, Alias = "b", FriendlyName = "archived", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
            var inbound = new RoomTransition { RoomId = external.Id, Alias = "in", FriendlyName = "inbound", DestinationRoomReferenceText = "target text", DestinationTransitionAliasText = "a text", ResolvedDestinationRoomId = target.Id, ResolvedDestinationTransitionId = activeTransition.Id };
            var unrelatedRoom = new Room { FriendlyName = "Unrelated", ReferenceId = "unrelated" };
            var unrelatedTransition = new RoomTransition { RoomId = unrelatedRoom.Id, Alias = "u", FriendlyName = "unrelated" };
            var staleRoomInbound = new RoomTransition { RoomId = external.Id, Alias = "sr", FriendlyName = "stale room", ResolvedDestinationRoomId = target.Id, ResolvedDestinationTransitionId = unrelatedTransition.Id };
            var staleTransitionInbound = new RoomTransition { RoomId = external.Id, Alias = "st", FriendlyName = "stale transition", ResolvedDestinationRoomId = unrelatedRoom.Id, ResolvedDestinationTransitionId = activeTransition.Id };
            var subroom = new Subroom { RoomId = target.Id, FriendlyName = "sub", ReferenceId = "sub" };
            var archivedSubroom = new Subroom { RoomId = target.Id, FriendlyName = "archived sub", ReferenceId = "sub2", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
            var connection = new SubroomConnection { RoomId = target.Id, Alias = "c", FriendlyName = "connection" };
            var archivedConnection = new SubroomConnection { RoomId = target.Id, Alias = "d", FriendlyName = "archived connection", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
            var check = new CheckLocation { RoomId = target.Id, FriendlyName = "check" };
            var archivedCheck = new CheckLocation { RoomId = target.Id, FriendlyName = "archived check", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
            await using (var db = fixture.CreateDbContext())
            {
                db.AddRange(target, external, activeTransition, archivedTransition, inbound, unrelatedRoom, unrelatedTransition, staleRoomInbound, staleTransitionInbound, subroom, archivedSubroom, connection, archivedConnection, check, archivedCheck);
                await db.SaveChangesAsync();
                var map = new Map { InGameId = "map" }; db.Add(map); await db.SaveChangesAsync(); var zone = new MapZone { MapId = map.Id, InGameId = "zone" }; db.Add(zone); await db.SaveChangesAsync();
                db.Add(new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = "target map text", ResolvedRoomId = target.Id }); await db.SaveChangesAsync();
            }
            var files = new SceneImageFileService(root); Directory.CreateDirectory(Path.GetDirectoryName(files.GetRoomImagePath(target.Id))!); await File.WriteAllTextAsync(files.GetRoomImagePath(target.Id), "image");
            var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture);

            var outcome = await commands.DeleteRoomAsync(target.Id);
            Assert.Equal(V2RoomLifecycleCommandStatus.Committed, outcome.Status); Assert.True(outcome.SceneImageDeleteElapsed < TimeSpan.FromMilliseconds(200)); Assert.False(files.Exists(target.Id));
            await using var verify = fixture.CreateDbContext();
            Assert.Null(await verify.Rooms.SingleOrDefaultAsync(x => x.Id == target.Id));
            Assert.Equal(0, await verify.Subrooms.CountAsync(x => x.RoomId == target.Id)); Assert.Equal(0, await verify.RoomTransitions.CountAsync(x => x.RoomId == target.Id));
            Assert.Equal(0, await verify.SubroomConnections.CountAsync(x => x.RoomId == target.Id)); Assert.Equal(0, await verify.CheckLocations.CountAsync(x => x.RoomId == target.Id));
            var retained = await verify.RoomTransitions.SingleAsync(x => x.Id == inbound.Id);
            Assert.Equal(("target text", "a text", (Guid?)null, (Guid?)null), (retained.DestinationRoomReferenceText, retained.DestinationTransitionAliasText, retained.ResolvedDestinationRoomId, retained.ResolvedDestinationTransitionId));
            var retainedTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == staleRoomInbound.Id);
            Assert.Equal(((Guid?)null, (Guid?)unrelatedTransition.Id), (retainedTransition.ResolvedDestinationRoomId, retainedTransition.ResolvedDestinationTransitionId));
            var retainedRoom = await verify.RoomTransitions.SingleAsync(x => x.Id == staleTransitionInbound.Id);
            Assert.Equal(((Guid?)unrelatedRoom.Id, (Guid?)null), (retainedRoom.ResolvedDestinationRoomId, retainedRoom.ResolvedDestinationTransitionId));
            var scene = await verify.MapScenes.SingleAsync(); Assert.Equal(("target map text", (Guid?)null), (scene.RoomReferenceText, scene.ResolvedRoomId));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
