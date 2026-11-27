using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services.Publishing;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class DistributedExportService(IDbContextFactory<LogicDbContext> dbContextFactory, TimeProvider timeProvider,
    SharedPublicationCoordinator publications, Authentication.LiveAccess access)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<CompleteExportOutcome> ExportCompleteAsync(bool includeAreaMap, CancellationToken cancellationToken = default)
    {
        using var publication = await publications.AcquireAsync(cancellationToken);
        await access.RequireAsync(token: cancellationToken);
        return await ExportCompleteAsync(dbContextFactory, timeProvider, includeAreaMap, publication, cancellationToken);
    }

    internal static Task<CompleteExportOutcome> ExportPreparedAsync(PublishingSnapshotFactory contexts,
        TimeProvider timeProvider, bool includeAreaMap, PublishingTimingScope timing, CancellationToken cancellationToken = default) =>
        ExportCompleteAsync(contexts, timeProvider, includeAreaMap, null, cancellationToken, timing);

    private static async Task<CompleteExportOutcome> ExportCompleteAsync(IDbContextFactory<LogicDbContext> dbContextFactory,
        TimeProvider timeProvider, bool includeAreaMap, SharedPublicationCoordinator.Admission? publication,
        CancellationToken cancellationToken, PublishingTimingScope? preparedTiming = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Only the frozen path supplies timing and can split the room read without
        // changing the consistency of the input across the child commands.
        var rooms = await MeasureAsync(PublishingMetric.RoomLoad,
            () => LoadRoomsAsync(db, null, null, cancellationToken, splitQuery: preparedTiming is not null));
        var groups = await MeasureAsync(PublishingMetric.GroupLoad,
            () => db.RoomGroups.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new DistributedRoomGroup(x.Id, x.FriendlyName, x.ZoneReferenceText, x.IsVirtual, x.SortOrder, x.CreatedUtc, x.UpdatedUtc)).ToListAsync(cancellationToken));
        if (InvalidLocationType(rooms) is { } invalid) return new CompleteExportInvalidLocationType(invalid);
        var envelope = new Version6RoomExportEnvelope(6, false, rooms, new DistributedRoomGroupingSnapshot(groups),
            includeAreaMap ? await MeasureAsync(PublishingMetric.AreaMapLoad, () => LoadAreaMapAsync(db, cancellationToken)) : null);
        publication?.Dispose();
        return new CompleteExported(Serialize(envelope, $"silksong-logic-{Timestamp(timeProvider)}.json", preparedTiming));

        Task<T> MeasureAsync<T>(PublishingMetric metric, Func<Task<T>> work) =>
            preparedTiming is null ? work() : preparedTiming.MeasureAsync(metric, work);
    }

    public async Task<CurrentRoomExportOutcome> ExportCurrentRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        using var publication = await publications.AcquireAsync(cancellationToken);
        await access.RequireAsync(token: cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rooms = await LoadRoomsAsync(db, roomId, cancellationToken);
        var room = rooms.SingleOrDefault();
        if (room is null) return new CurrentRoomMissing(roomId);
        if (InvalidLocationType(rooms) is { } invalid) return new CurrentRoomExportInvalidLocationType(invalid);
        var groups = room.RoomGroupId is Guid groupId ? await db.RoomGroups.AsNoTracking().Where(x => x.Id == groupId).Select(x => new DistributedRoomGroup(x.Id, x.FriendlyName, x.ZoneReferenceText, x.IsVirtual, x.SortOrder, x.CreatedUtc, x.UpdatedUtc)).ToListAsync(cancellationToken) : [];
        var envelope = new Version6RoomExportEnvelope(6, true, rooms, new DistributedRoomGroupingSnapshot(groups), null);
        publication.Dispose();
        var prefix = string.IsNullOrWhiteSpace(room.ReferenceId) ? "empty-room" : room.ReferenceId;
        return new CurrentRoomExported(Serialize(envelope, $"{prefix}-{Timestamp(timeProvider)}.json"));
    }

    public async Task<ZoneExportOutcome> ExportZoneAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        using var publication = await publications.AcquireAsync(cancellationToken);
        await access.RequireAsync(token: cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var groupId = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId).Select(x => x.RoomGroupId).SingleOrDefaultAsync(cancellationToken);
        if (groupId is not Guid id) return new ZoneExportUnavailable(roomId);

        var group = await db.RoomGroups.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new DistributedRoomGroup(x.Id, x.FriendlyName, x.ZoneReferenceText, x.IsVirtual, x.SortOrder, x.CreatedUtc, x.UpdatedUtc))
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null) return new ZoneExportUnavailable(roomId);

        var rooms = await LoadRoomsAsync(db, null, id, cancellationToken);
        if (InvalidLocationType(rooms) is { } invalid) return new ZoneExportInvalidLocationType(invalid);
        var envelope = new Version6RoomExportEnvelope(6, true, rooms, new DistributedRoomGroupingSnapshot([group]), null);
        publication.Dispose();
        return new ZoneExported(Serialize(envelope, $"zone-{id:D}-{Timestamp(timeProvider)}.json"));
    }

    private static Task<List<DistributedRoomDocument>> LoadRoomsAsync(LogicDbContext db, Guid? roomId, CancellationToken cancellationToken) =>
        LoadRoomsAsync(db, roomId, null, cancellationToken);

    private static async Task<List<DistributedRoomDocument>> LoadRoomsAsync(LogicDbContext db, Guid? roomId, Guid? roomGroupId,
        CancellationToken cancellationToken, bool splitQuery = false)
    {
        var rooms = db.Rooms.AsNoTracking();
        if (splitQuery) rooms = rooms.AsSplitQuery();
        if (roomId is Guid id) rooms = rooms.Where(x => x.Id == id);
        if (roomGroupId is Guid groupId) rooms = rooms.Where(x => x.RoomGroupId == groupId);
        return await rooms.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new DistributedRoomDocument(x.Id, x.RoomGroupId, x.ReferenceId, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY, x.SceneImagePixelsPerUnitX, x.SceneImagePixelsPerUnitY, x.SceneImagePixelOffsetX, x.SceneImagePixelOffsetY, x.UseSceneImage, x.UseUploadedSceneImage, x.SceneImageOriginalHash, x.SceneImageCropHash, x.SortOrder, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc,
            x.Subrooms.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedSubroom(y.Id, y.ReferenceId, y.FriendlyName, y.Notes, y.SceneUnitX, y.SceneUnitY, y.SceneUnitWidth, y.SceneUnitHeight, y.EnableAnnotation, y.SortOrder, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.Transitions.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedTransition(y.Id, y.Alias, y.FriendlyName, y.InGameId, y.InGamePositionX, y.InGamePositionY, y.InGamePositionZ, y.LocalPositionX, y.LocalPositionY, y.LocalPositionZ, y.AnnotationSceneUnitX, y.AnnotationSceneUnitY, y.EnableAnnotation, y.SourceSubroomReferenceText, y.DestinationRoomReferenceText, y.DestinationTransitionAliasText, y.Requirements, y.Notes, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc, y.IsUnsafeArrival)).ToList(),
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

    private static DistributedExportResult Serialize(Version6RoomExportEnvelope envelope, string fileName, PublishingTimingScope? timing = null)
    {
        var json = timing is null ? JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions)
            : timing.Measure(PublishingMetric.JsonSerialization, () => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
        return new(fileName, envelope, json);
    }
    private static string Timestamp(TimeProvider timeProvider) => timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

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
