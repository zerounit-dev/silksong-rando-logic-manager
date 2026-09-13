using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class DistributedExportService(IDbContextFactory<LogicDbContext> dbContextFactory, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<CompleteExportOutcome> ExportCompleteAsync(bool includeAreaMap, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rooms = await LoadRoomsAsync(db, null, cancellationToken);
        var groups = await db.RoomGroups.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new DistributedRoomGroup(x.Id, x.FriendlyName, x.ZoneReferenceText, x.SortOrder, x.CreatedUtc, x.UpdatedUtc)).ToListAsync(cancellationToken);
        if (InvalidLocationType(rooms) is { } invalid) return new CompleteExportInvalidLocationType(invalid);
        var envelope = new Version3RoomExportEnvelope(3, false, rooms, new DistributedRoomGroupingSnapshot(groups), includeAreaMap ? await LoadAreaMapAsync(db, cancellationToken) : null);
        return new CompleteExported(Serialize(envelope, $"silksong-logic-{Timestamp()}.json"));
    }

    public async Task<CurrentRoomExportOutcome> ExportCurrentRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rooms = await LoadRoomsAsync(db, roomId, cancellationToken);
        var room = rooms.SingleOrDefault();
        if (room is null) return new CurrentRoomMissing(roomId);
        if (InvalidLocationType(rooms) is { } invalid) return new CurrentRoomExportInvalidLocationType(invalid);
        var groups = room.RoomGroupId is Guid groupId ? await db.RoomGroups.AsNoTracking().Where(x => x.Id == groupId).Select(x => new DistributedRoomGroup(x.Id, x.FriendlyName, x.ZoneReferenceText, x.SortOrder, x.CreatedUtc, x.UpdatedUtc)).ToListAsync(cancellationToken) : [];
        var envelope = new Version3RoomExportEnvelope(3, true, rooms, new DistributedRoomGroupingSnapshot(groups), null);
        var prefix = string.IsNullOrWhiteSpace(room.ReferenceId) ? "empty-room" : room.ReferenceId;
        return new CurrentRoomExported(Serialize(envelope, $"{prefix}-{Timestamp()}.json"));
    }

    public async Task<ZoneExportOutcome> ExportZoneAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var groupId = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId).Select(x => x.RoomGroupId).SingleOrDefaultAsync(cancellationToken);
        if (groupId is not Guid id) return new ZoneExportUnavailable(roomId);

        var group = await db.RoomGroups.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new DistributedRoomGroup(x.Id, x.FriendlyName, x.ZoneReferenceText, x.SortOrder, x.CreatedUtc, x.UpdatedUtc))
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null) return new ZoneExportUnavailable(roomId);

        var rooms = await LoadRoomsAsync(db, null, id, cancellationToken);
        if (InvalidLocationType(rooms) is { } invalid) return new ZoneExportInvalidLocationType(invalid);
        var envelope = new Version3RoomExportEnvelope(3, true, rooms, new DistributedRoomGroupingSnapshot([group]), null);
        return new ZoneExported(Serialize(envelope, $"zone-{id:D}-{Timestamp()}.json"));
    }

    private static Task<List<DistributedRoomDocument>> LoadRoomsAsync(LogicDbContext db, Guid? roomId, CancellationToken cancellationToken) =>
        LoadRoomsAsync(db, roomId, null, cancellationToken);

    private static async Task<List<DistributedRoomDocument>> LoadRoomsAsync(LogicDbContext db, Guid? roomId, Guid? roomGroupId, CancellationToken cancellationToken)
    {
        var rooms = db.Rooms.AsNoTracking();
        if (roomId is Guid id) rooms = rooms.Where(x => x.Id == id);
        if (roomGroupId is Guid groupId) rooms = rooms.Where(x => x.RoomGroupId == groupId);
        return await rooms.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new DistributedRoomDocument(x.Id, x.RoomGroupId, x.ReferenceId, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.SceneUnitWidth, x.SceneUnitHeight, x.SceneImageScaleXPercent, x.SceneImageScaleYPercent, x.SceneImagePanXPercent, x.SceneImagePanYPercent, x.IsSceneImageStale, x.SortOrder, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc,
            x.Subrooms.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedSubroom(y.Id, y.ReferenceId, y.FriendlyName, y.Notes, y.SceneUnitX, y.SceneUnitY, y.SceneUnitWidth, y.SceneUnitHeight, y.EnableAnnotation, y.SortOrder, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.Transitions.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedTransition(y.Id, y.Alias, y.FriendlyName, y.InGameId, y.InGamePositionX, y.InGamePositionY, y.InGamePositionZ, y.LocalPositionX, y.LocalPositionY, y.LocalPositionZ, y.AnnotationSceneUnitX, y.AnnotationSceneUnitY, y.EnableAnnotation, y.SourceSubroomReferenceText, y.DestinationRoomReferenceText, y.DestinationTransitionAliasText, y.Requirements, y.Notes, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.Connections.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedConnection(y.Id, y.Alias, y.FriendlyName, y.SourceSubroomReferenceText, y.DestinationSubroomReferenceText, y.Requirements, y.Notes, y.EnableAnnotation, y.SceneUnitX, y.SceneUnitY, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.CheckLocations.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedCheck(y.Id, y.FriendlyName, y.InGameId, y.InGamePositionX, y.InGamePositionY, y.InGamePositionZ, y.LocalPositionX, y.LocalPositionY, y.LocalPositionZ, y.AnnotationSceneUnitX, y.AnnotationSceneUnitY, y.SubroomReferenceText, y.Requirements, y.Notes, y.LocationType, y.EnableAnnotation, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList())).ToListAsync(cancellationToken);
    }

    private static async Task<DistributedAreaMapSnapshot> LoadAreaMapAsync(LogicDbContext db, CancellationToken cancellationToken)
    {
        var maps = await db.Maps.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new DistributedMap(
            x.Id, x.InGameId, x.FriendlyName, x.SortOrder, x.MapUnitMinX, x.MapUnitMinY, x.MapUnitMaxX, x.MapUnitMaxY,
            x.Overlays.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedMapOverlay(
                y.Id, y.FriendlyName, y.ImageAssetKey, y.ScaleXPercent, y.ScaleYPercent, y.LeftOffsetPercent, y.BottomOffsetPercent, y.SortOrder)).ToList(),
            x.Zones.OrderBy(y => y.InGameId).ThenBy(y => y.Id).Select(y => new DistributedMapZone(
                y.Id, y.InGameId, y.FriendlyName, y.MapUnitMinX, y.MapUnitMinY, y.MapUnitMaxX, y.MapUnitMaxY,
                y.Scenes.OrderBy(z => z.InGameId).ThenBy(z => z.Id).Select(z => new DistributedMapScene(
                    z.Id, z.InGameId, z.FriendlyName, z.RoomReferenceText,
                    z.Chunks.OrderBy(a => a.CacheIndex).ThenBy(a => a.Id).Select(a => new DistributedMapChunk(
                        a.Id, a.CacheIndex, a.InitialState, a.MapUnitMinX, a.MapUnitMinY, a.MapUnitMaxX, a.MapUnitMaxY, a.MapUnitZ)).ToList())).ToList())).ToList())).ToListAsync(cancellationToken);
        return new DistributedAreaMapSnapshot(maps);
    }

    private static DistributedExportInvalidLocationType? InvalidLocationType(IEnumerable<DistributedRoomDocument> rooms)
    {
        foreach (var room in rooms)
            foreach (var check in room.Checks)
                if (check.LocationType is not null && !CheckLocationTypeCatalogue.IsRecognized(check.LocationType))
                    return new(room.Id, room.FriendlyName, check.Id, check.FriendlyName);
        return null;
    }

    private DistributedExportResult Serialize(Version3RoomExportEnvelope envelope, string fileName) => new(fileName, envelope, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
    private string Timestamp() => timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new ExportUtcDateTimeConverter());
        return options;
    }

    private sealed class ExportUtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
