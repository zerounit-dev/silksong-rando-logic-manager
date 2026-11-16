using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Fixed DOM readers for the four remaining local SQL replies. These do not configure interop's
// serializer or any shared HTTP/domain converter, and never reconstruct SQL cells.
internal static class LocalNamedReadReader
{
    private static JsonException Invalid() => new("Invalid named local SQLite result.");
    private static JsonElement P(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : throw Invalid();
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    private static string S(JsonElement value, string name) => Text(P(value, name));
    private static string? N(JsonElement value, string name) { var x = P(value, name); return x.ValueKind == JsonValueKind.Null ? null : Text(x); }
    private static int I(JsonElement value, string name) { var x = P(value, name); return x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) ? n : throw Invalid(); }
    private static bool Boolean(JsonElement value) => value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid() };
    private static bool B(JsonElement value, string name) => Boolean(P(value, name));
    private static bool? NB(JsonElement value, string name) { var x = P(value, name); return x.ValueKind == JsonValueKind.Null ? null : Boolean(x); }
    // Persisted source IDs intentionally retain Guid.Parse, not JSON's narrower GUID grammar.
    private static Guid G(JsonElement value, string name) => Guid.Parse(S(value, name));
    private static Guid? NG(JsonElement value, string name) => N(value, name) is { } text ? Guid.Parse(text) : null;
    private static double D(JsonElement value, string name) => double.Parse(S(value, name), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static double? ND(JsonElement value, string name) => N(value, name) is { } text ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : null;
    private static DateTime U(JsonElement value, string name) => DateTime.SpecifyKind(DateTime.ParseExact(S(value, name),
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc);
    private static DateTime Ticks(JsonElement value, string name) => new(long.Parse(S(value, name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), DateTimeKind.Utc);
    private static RoomStatus Status(JsonElement value) => I(value, "completionStatus") switch {
        0 => RoomStatus.Neutral, 1 => RoomStatus.Success, 2 => RoomStatus.Warning, 3 => RoomStatus.Danger, _ => throw Invalid() };
    private static ImmutableArray<T> List<T>(JsonElement value, string name, Func<JsonElement, T> read, Func<T, Guid>? identity = null)
    {
        var source = P(value, name);
        if (source.ValueKind != JsonValueKind.Array) throw Invalid();
        var result = ImmutableArray.CreateBuilder<T>(source.GetArrayLength());
        var ids = identity is null ? null : new HashSet<Guid>();
        foreach (var item in source.EnumerateArray())
        {
            var next = read(item);
            if (identity is not null && (identity(next) == Guid.Empty || !ids!.Add(identity(next)))) throw Invalid();
            result.Add(next);
        }
        return result.MoveToImmutable();
    }
    private static RoomDocumentObservation Observation(JsonElement x) => new(G(x, "roomId"), Ticks(x, "cacheFlagUtc"));
    private static RoomRowObservation Row(JsonElement x) => new(G(x, "entityId"), U(x, "updatedUtc"), B(x, "isArchived"));
    private static CatalogueGroup Group(JsonElement x) => new(G(x, "id"), S(x, "friendlyName"), I(x, "sortOrder"),
        N(x, "zoneReferenceText"), NG(x, "resolvedMapZoneId"), B(x, "isVirtual"), U(x, "updatedUtc"));
    private static RoomInGameMetadata Metadata(JsonElement x) => new(N(x, "inGameId"), ND(x, "inGamePositionX"), ND(x, "inGamePositionY"),
        ND(x, "inGamePositionZ"), ND(x, "localPositionX"), ND(x, "localPositionY"), ND(x, "localPositionZ"),
        ND(x, "annotationSceneUnitX"), ND(x, "annotationSceneUnitY"));
    private static RoomSceneImageBaseline Image(JsonElement x) => new(D(x, "minX"), D(x, "minY"), ND(x, "maxX"), ND(x, "maxY"),
        ND(x, "pixelsPerUnitX"), ND(x, "pixelsPerUnitY"), ND(x, "pixelOffsetX"), ND(x, "pixelOffsetY"),
        B(x, "useSceneImage"), B(x, "useUploadedSceneImage"), B(x, "stale"), N(x, "originalHash"), N(x, "cropHash"), U(x, "updatedUtc"));

    // Inner query state/CAS only; real outer release admission precedes this reader.
    internal static (LocalCacheState? State, bool Conflict) Header(JsonElement value)
    {
        var state = P(value, "state");
        var conflict = value.TryGetProperty("conflict", out var flag) && Boolean(flag);
        if (state.ValueKind == JsonValueKind.Null) return (null, conflict);
        var checkpoint = P(state, "checkpoint");
        return (new(S(state, "stateId"), new(I(checkpoint, "format"), P(checkpoint, "runId").GetGuid(),
            P(checkpoint, "startedUtc").GetDateTime(), P(checkpoint, "throughUtc").GetDateTime(), S(checkpoint, "schema"))), conflict);
    }

    internal static ImmutableArray<LocalRoomInventory> Inventory(JsonElement value, string name = "rooms") => List(value, name,
        // Existing inventory uses standard JSON GUIDs and does not parse ticks here.
        x => new LocalRoomInventory(P(x, "id").GetGuid(), S(x, "cacheFlagUtc"), I(x, "completionStatus")));

    internal static LocalVocabularySource<VocabularyDefinition> Vocabulary(JsonElement value)
    {
        var x = P(value, "vocabulary");
        ImmutableArray<VocabularyDefinition> Definitions(string name, bool predicate = false) => List(x, name, d => {
            var input = predicate ? S(d, "inputSyntax") : N(d, "inputSyntax");
            if (!predicate && input is not null) throw Invalid();
            return new VocabularyDefinition(G(d, "id"), S(d, "name"), N(d, "category"), I(d, "sortOrder"),
                S(d, "aliases"), input, S(d, "output"), S(d, "notes"));
        }, d => d.Id);
        return new(Definitions("predicates", true), Definitions("crests"), Definitions("methods"),
            Definitions("items"), Definitions("countables"), Definitions("souls"));
    }

    internal static LocalMapsSource Maps(JsonElement value) => new(
        List(value, "maps", x => new MapDataMap(G(x, "id"), I(x, "sortOrder"), ND(x, "minX"), ND(x, "minY"), ND(x, "maxX"), ND(x, "maxY")), x => x.Id),
        List(value, "zones", x => new MapDataZone(G(x, "id"), G(x, "mapId"), S(x, "inGameId"), ND(x, "minX"), ND(x, "minY"), ND(x, "maxX"), ND(x, "maxY")), x => x.Id),
        List(value, "scenes", x => new MapDataScene(G(x, "id"), G(x, "mapZoneId"), S(x, "inGameId"), N(x, "roomReferenceText"), NG(x, "resolvedRoomId")), x => x.Id),
        List(value, "chunks", x => new MapDataChunk(G(x, "id"), G(x, "mapSceneId"), I(x, "cacheIndex"), ND(x, "minX"), ND(x, "minY"), ND(x, "maxX"), ND(x, "maxY"), ND(x, "z")), x => x.Id),
        // Decode every overlay before the managed materializer's stable first selection.
        List(value, "overlays", x => new LocalOverlaySource(G(x, "id"), G(x, "mapId"), S(x, "imageAssetKey"), I(x, "sortOrder"),
            D(x, "scaleXPercent"), D(x, "scaleYPercent"), D(x, "leftOffsetPercent"), D(x, "bottomOffsetPercent")), x => x.Id));

    internal static LocalRoomSource Room(JsonElement value, Guid roomId)
    {
        var rooms = List(value, "rooms", r => {
            var o = Observation(P(r, "observation")); var row = Row(P(r, "row")); var v = P(r, "values");
            if (o.RoomId != roomId || row.EntityId != o.RoomId) throw Invalid();
            return new LocalRoomHeaderSource(o, row, new(S(v, "friendlyName"), S(v, "referenceId"), N(v, "inGameId"), N(v, "contributors"), N(v, "comments")),
                NG(r, "roomGroupId"), I(r, "sortOrder"), Status(r), Image(P(r, "sceneImage")));
        }, r => r.Observation.RoomId);
        Guid Owner(JsonElement x) { var owner = G(x, "roomId"); if (owner != roomId) throw Invalid(); return owner; }
        if (rooms.IsEmpty)
        {
            // The old missing path consumes child identity/owner, not child bodies,
            // referenced Groups or full document/order context. Preserve that boundary.
            foreach (var family in new[] { "subrooms", "transitions", "connections", "checks" })
                _ = List(value, family, x => { Owner(x); return G(x, "id"); }, id => id);
            return new(rooms, [], [], [], [], [], [], []);
        }
        var subrooms = List(value, "subrooms", x => {
            var owner = Owner(x); var s = P(x, "snapshot"); var v = P(s, "values"); var g = P(s, "geometry");
            return new LocalSubroomSource(owner, new(Row(P(s, "observation")), I(s, "sortOrder"), new(S(v, "friendlyName"), S(v, "referenceId"), N(v, "notes")),
                new(ND(g, "x"), ND(g, "y"), ND(g, "width"), ND(g, "height"), B(g, "enableAnnotation"))));
        }, x => x.Snapshot.Observation.EntityId);
        var transitions = List(value, "transitions", x => {
            var owner = Owner(x); var s = P(x, "snapshot"); var v = P(s, "values");
            return new LocalTransitionSource(owner, new(Row(P(s, "observation")), I(s, "sortOrder"),
                new(S(v, "alias"), S(v, "friendlyName"), N(v, "sourceSubroomReferenceText"), N(v, "destinationRoomReferenceText"), N(v, "destinationTransitionAliasText"),
                    S(v, "requirements"), S(v, "notes"), B(v, "isTodo"), NB(v, "isVerified"), B(v, "isUnsafeArrival")),
                Metadata(P(s, "metadata")), B(s, "enableAnnotation"), NB(s, "requirementsParseSucceeded"), NG(s, "resolvedSourceSubroomId"),
                NG(s, "resolvedDestinationRoomId"), NG(s, "resolvedDestinationTransitionId")));
        }, x => x.Snapshot.Observation.EntityId);
        var connections = List(value, "connections", x => {
            var owner = Owner(x); var s = P(x, "snapshot"); var v = P(s, "values");
            return new LocalConnectionSource(owner, new(Row(P(s, "observation")), I(s, "sortOrder"),
                new(S(v, "alias"), S(v, "friendlyName"), S(v, "sourceSubroomReferenceText"), S(v, "destinationSubroomReferenceText"),
                    S(v, "requirements"), S(v, "notes"), B(v, "isTodo"), NB(v, "isVerified")),
                ND(s, "sceneUnitX"), ND(s, "sceneUnitY"), B(s, "enableAnnotation"), NB(s, "requirementsParseSucceeded"),
                NG(s, "resolvedSourceSubroomId"), NG(s, "resolvedDestinationSubroomId")));
        }, x => x.Snapshot.Observation.EntityId);
        var checks = List(value, "checks", x => {
            var owner = Owner(x); var s = P(x, "snapshot"); var v = P(s, "values");
            return new LocalCheckSource(owner, new(Row(P(s, "observation")), I(s, "sortOrder"),
                new(S(v, "friendlyName"), N(v, "subroomReferenceText"), S(v, "requirements"), S(v, "notes"), N(v, "locationType"), B(v, "isTodo"), NB(v, "isVerified")),
                Metadata(P(s, "metadata")), B(s, "enableAnnotation"), NB(s, "requirementsParseSucceeded"), NG(s, "resolvedSubroomId")));
        }, x => x.Snapshot.Observation.EntityId);
        var groups = List(value, "groups", Group, x => x.Id);
        var groupId = rooms.Single().RoomGroupId;
        if (groups.Length != (groupId is null ? 0 : 1) || groups.SingleOrDefault()?.Id != groupId) throw Invalid();
        return new(rooms, subrooms, transitions, connections, checks, groups,
            List(value, "roomOrder", x => x.GetGuid()), List(value, "groupOrder", x => x.GetGuid()));
    }
}
