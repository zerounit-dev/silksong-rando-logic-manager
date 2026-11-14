using System.Collections.Immutable;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

internal static class LocalRoomMaterializer
{
    private static async Task<ImmutableArray<T>> MapAsync<TSource, T>(IEnumerable<TSource> rows,
        Func<TSource, T> map, Func<Task> yield, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<T>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested(); result.Add(map(row));
            if (result.Count % 128 == 0) { await yield(); token.ThrowIfCancellationRequested(); }
        }
        return result.ToImmutable();
    }
    private static async Task<ILookup<Guid, T>> ChildrenAsync<T>(IEnumerable<T> source, Func<T, Guid> ownerId, Guid roomId,
        Func<Task> yield, CancellationToken token)
    {
        var rows = new List<(Guid Owner, T Row)>();
        foreach (var row in source)
        {
            token.ThrowIfCancellationRequested();
            var owner = ownerId(row);
            if (owner != roomId) throw new JsonException("A named Room query returned another owner's child.");
            rows.Add((owner, row));
            if (rows.Count % 128 == 0) { await yield(); token.ThrowIfCancellationRequested(); }
        }
        return rows.ToLookup(x => x.Owner, x => x.Row);
    }
    internal static async Task<RoomCompleteDocument?> RoomAsync(LocalRoomSource read, Guid roomId,
        IReadOnlyDictionary<Guid, bool> images, Func<Task> yield, CancellationToken token)
    {
        // Same family/checkpoint traversal as the former cell materializer. Startup
        // callbacks remain synchronously completing; ordinary owners still yield.
        var subrooms = await ChildrenAsync(read.Subrooms, x => x.RoomId, roomId, yield, token);
        var transitions = await ChildrenAsync(read.Transitions, x => x.RoomId, roomId, yield, token);
        var connections = await ChildrenAsync(read.Connections, x => x.RoomId, roomId, yield, token);
        var checks = await ChildrenAsync(read.Checks, x => x.RoomId, roomId, yield, token);
        if (read.Rooms.IsEmpty) return null;
        var source = read.Rooms.Single();
        token.ThrowIfCancellationRequested();
        if (source.Observation.RoomId != roomId) throw new JsonException("A named Room query returned another Room.");
        bool? exists = images.TryGetValue(roomId, out var known) ? known : null;
        var header = new RoomHeaderSnapshot(source.Row, source.Values, source.RoomGroupId, source.SortOrder, source.SceneImage)
            { Metadata = new(exists), CompletionStatus = source.CompletionStatus };
        var room = new RoomScalarSnapshot(source.Observation, header,
            await MapAsync(subrooms[roomId], x => x.Snapshot, yield, token),
            await MapAsync(transitions[roomId], x => x.Snapshot, yield, token),
            await MapAsync(connections[roomId], x => x.Snapshot, yield, token),
            await MapAsync(checks[roomId], x => x.Snapshot, yield, token));
        var groups = read.Groups;
        if (groups.Length != (header.RoomGroupId is null ? 0 : 1)
            || groups.SingleOrDefault()?.Id != header.RoomGroupId) throw new JsonException("Wrong demanded Room group context.");
        var context = new RoomReadContext(groups.SingleOrDefault(), read.RoomOrder, read.GroupOrder);
        var document = Document(room, context);
        RoomDocumentReconciliation.Validate(document);
        return document;
    }

    internal static RoomCompleteDocument Document(RoomScalarSnapshot room, RoomReadContext context)
    {
        var h = room.Header; var i = h.SceneImage;
        var scene = h.Observation.IsArchived ? null : SceneLayoutMapper.Map(
            new(room.Observation.RoomId, false, i.MinX, i.MinY, i.MaxX, i.MaxY, i.PixelsPerUnitX, i.PixelsPerUnitY,
                i.PixelOffsetX, i.PixelOffsetY, i.UseSceneImage, i.UseUploadedSceneImage, i.Stale, i.UpdatedUtc),
            room.Subrooms.Where(x => !x.Observation.IsArchived).Select(x => new SceneSubroomLoad(x.Observation.EntityId,
                x.Values.FriendlyName, x.Geometry.X, x.Geometry.Y, x.Geometry.Width, x.Geometry.Height, x.Geometry.EnableAnnotation)).ToArray(),
            room.Transitions.Where(x => !x.Observation.IsArchived && x.EnableAnnotation).Select(x => new SceneTransitionLoad(x.Observation.EntityId,
                x.Values.Alias, x.Values.FriendlyName, x.Metadata.AnnotationSceneUnitX, x.Metadata.AnnotationSceneUnitY, x.ResolvedSourceSubroomId)).ToArray(),
            room.Checks.Where(x => !x.Observation.IsArchived && x.EnableAnnotation).Select(x => new SceneCheckLoad(x.Observation.EntityId,
                x.Values.FriendlyName, x.Metadata.AnnotationSceneUnitX, x.Metadata.AnnotationSceneUnitY, x.ResolvedSubroomId, x.Values.LocationType)).ToArray(),
            room.Connections.Where(x => !x.Observation.IsArchived).Select(x => new SceneConnectionLoad(x.Observation.EntityId,
                x.Values.Alias, x.Values.FriendlyName, x.Values.SourceSubroomReferenceText, x.Values.DestinationSubroomReferenceText,
                x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY)).ToArray(),
            h.Metadata.ImageExists);
        return new(room.Observation, h, room.Subrooms, room.Transitions, room.Connections, room.Checks, scene) { ReadContext = context };
    }
}
