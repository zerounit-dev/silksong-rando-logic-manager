using System.Globalization;
using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class DistributedImportPackageParser
{
    public DistributedImportParseOutcome Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            var root = new ObjectReader(document.RootElement, "$", RequireObject);
            var version = root.Int("exportVersion");
            if (version is < 1 or > 6) throw new PackageFormatException("$.exportVersion is unsupported.");

            var hasRooms = root.Has("rooms");
            bool? partial = null;
            if (hasRooms)
            {
                if (version == 1) throw new PackageFormatException("$.rooms is not supported by Version 1.");
                partial = root.Bool("isPartialRoomDump");
            }
            else if (root.Has("isPartialRoomDump")) throw new PackageFormatException("$.isPartialRoomDump requires $.rooms.");

            var identities = new IdentityRegistry();
            var areaMap = root.Optional("areaMap") is JsonElement map ? ReadAreaMap(map, "$.areaMap", identities) : null;
            var groupings = root.Optional("roomGroupings") is JsonElement groups ? ReadGroups(groups, "$.roomGroupings", identities, version) : null;
            var rooms = hasRooms ? root.Array("rooms").Select((x, i) => ReadRoom(x, $"$.rooms[{i}]", identities, version)).ToArray() : [];
            ValidateStructuralMapIdentities(areaMap);
            ValidatePartialRoomScope(partial, groupings, rooms);
            var package = new DistributedImportPackage(version, partial, areaMap, groupings, rooms);
            return new DistributedImportPackageValidated(package, Summarize(package));
        }
        catch (JsonException error) { return new DistributedImportPackageRejected([$"Invalid JSON: {error.Message}"]); }
        catch (PackageFormatException error) { return new DistributedImportPackageRejected([error.Message]); }
    }

    private static void ValidatePartialRoomScope(bool? partial, DistributedRoomGroupingSnapshot? groupings, IReadOnlyList<DistributedRoomDocument> rooms)
    {
        if (partial != true) return;
        if (rooms.Count == 0) throw new PackageFormatException("$.rooms must contain at least one room for a partial room dump.");
        if (rooms.All(x => x.RoomGroupId is null))
        {
            if (rooms.Count != 1) throw new PackageFormatException("An ungrouped partial room dump must contain exactly one room.");
            if (groupings is null || groupings.Groups.Count != 0) throw new PackageFormatException("An ungrouped partial room dump requires empty $.roomGroupings.groups context.");
            return;
        }

        var groupId = rooms[0].RoomGroupId;
        if (groupId is null || rooms.Any(x => x.RoomGroupId != groupId)) throw new PackageFormatException("A grouped partial room dump must assign every room to one non-null room group.");
        if (groupings is null || groupings.Groups.Count != 1 || groupings.Groups[0].Id != groupId)
            throw new PackageFormatException("A grouped partial room dump requires exactly its one room group in $.roomGroupings.groups.");
    }

    public DistributedImportParseOutcome ComposePartialSelection(IReadOnlyList<DistributedImportFilePackage> files)
    {
        if (files.Count == 1) return new DistributedImportPackageValidated(files[0].Package, Summarize(files[0].Package));
        if (files.Count == 0) return new DistributedImportPackageRejected(["Select at least one JSON package."]);

        var errors = new List<string>();
        var groups = new Dictionary<Guid, DistributedRoomGroup>();
        var rooms = new List<DistributedRoomDocument>();
        var identities = new Dictionary<Guid, (string Kind, Guid? Owner, string FileName)>();
        var sources = new Dictionary<Guid, List<string>>();
        var version = files[0].Package.ExportVersion;
        foreach (var file in files)
        {
            var package = file.Package;
            if (package.ExportVersion != version) errors.Add($"{files[0].FileName}, {file.FileName}: export versions must match.");
            if (package.IsPartialRoomDump != true || package.Rooms.Count == 0) errors.Add($"{file.FileName}: multi-file selection requires a partial package containing rooms.");
            if (package.AreaMap is not null) errors.Add($"{file.FileName}: an areaMap snapshot is not allowed in a multi-file selection, even when empty.");
            foreach (var group in package.RoomGroupings?.Groups ?? [])
            {
                Add(group.Id, "room group", null, file.FileName);
                if (groups.TryGetValue(group.Id, out var previous))
                {
                    if (previous != group) errors.Add($"{identities[group.Id].FileName}, {file.FileName}: repeated room-group {group.Id} context must match every exchanged field exactly.");
                }
                else groups.Add(group.Id, group);
            }
            foreach (var room in package.Rooms)
            {
                Add(room.Id, "room", null, file.FileName);
                foreach (var child in room.Subrooms) Add(child.Id, "subroom", room.Id, file.FileName);
                foreach (var child in room.Transitions) Add(child.Id, "transition", room.Id, file.FileName);
                foreach (var child in room.Connections) Add(child.Id, "connection", room.Id, file.FileName);
                foreach (var child in room.Checks) Add(child.Id, "check", room.Id, file.FileName);
                rooms.Add(room);
            }
        }
        if (errors.Count != 0) return new DistributedImportPackageRejected(errors);
        var combined = new DistributedImportPackage(version, true, null, new(groups.Values.ToArray()), rooms.ToArray())
        {
            SourceFileNames = sources.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToArray())
        };
        return new DistributedImportPackageValidated(combined, Summarize(combined));

        void Add(Guid id, string kind, Guid? owner, string fileName)
        {
            if (!sources.TryGetValue(id, out var names)) sources.Add(id, names = []);
            names.Add(fileName);
            if (!identities.TryGetValue(id, out var prior)) { identities.Add(id, (kind, owner, fileName)); return; }
            if (prior.Kind == "room group" && kind == "room group") return;
            var reason = prior.Kind != kind ? $"GUID {id} is reused across {prior.Kind} and {kind} entities."
                : prior.Owner != owner ? $"{kind} GUID {id} is reused under different room owners."
                : $"{kind} GUID {id} overlaps; identical content is not permitted either.";
            errors.Add($"{prior.FileName}, {fileName}: {reason}");
        }
    }

    private static DistributedImportPackageSummary Summarize(DistributedImportPackage package) => new(
        package.ExportVersion, package.IsPartialRoomDump, package.AreaMap?.Maps.Count ?? 0, package.RoomGroupings?.Groups.Count ?? 0,
        package.Rooms.Count, package.Rooms.Sum(x => x.Subrooms.Count), package.Rooms.Sum(x => x.Transitions.Count),
        package.Rooms.Sum(x => x.Connections.Count), package.Rooms.Sum(x => x.Checks.Count));

    // These are imported structural identities, not authored-data duplicate diagnostics.
    // Check the complete snapshot before it can reach any selected map transaction.
    private static void ValidateStructuralMapIdentities(DistributedAreaMapSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zones = new HashSet<(Guid Parent, string Id)>();
        var scenes = new HashSet<(Guid Parent, string Id)>();
        var chunks = new HashSet<(Guid Parent, int Index)>();
        foreach (var map in snapshot.Maps)
        {
            if (!maps.Add(map.InGameId)) throw new PackageFormatException($"$.areaMap.maps has duplicate map structural identity '{map.InGameId}'.");
            foreach (var zone in map.Zones)
            {
                if (!zones.Add((map.Id, zone.InGameId.ToUpperInvariant()))) throw new PackageFormatException($"$.areaMap.maps has duplicate zone structural identity '{zone.InGameId}' under map {map.Id}.");
                foreach (var scene in zone.Scenes)
                {
                    if (!scenes.Add((zone.Id, scene.InGameId.ToUpperInvariant()))) throw new PackageFormatException($"$.areaMap.maps has duplicate scene structural identity '{scene.InGameId}' under zone {zone.Id}.");
                    foreach (var chunk in scene.Chunks)
                        if (!chunks.Add((scene.Id, chunk.CacheIndex))) throw new PackageFormatException($"$.areaMap.maps has duplicate chunk structural identity '{chunk.CacheIndex}' under scene {scene.Id}.");
                }
            }
        }
    }

    private static DistributedAreaMapSnapshot ReadAreaMap(JsonElement value, string path, IdentityRegistry ids)
    {
        var r = new ObjectReader(value, path, RequireObject);
        return new DistributedAreaMapSnapshot(r.Array("maps").Select((x, i) => ReadMap(x, $"{path}.maps[{i}]", ids)).ToArray());
    }
    private static DistributedMap ReadMap(JsonElement value, string path, IdentityRegistry ids)
    {
        var r = new ObjectReader(value, path, RequireObject); var id = r.Guid("id"); ids.Add(id, "map", path);
        return new DistributedMap(id, r.String("inGameId"), r.NullableString("friendlyName"), r.Int("sortOrder"), r.NullableNumber("mapUnitMinX"), r.NullableNumber("mapUnitMinY"), r.NullableNumber("mapUnitMaxX"), r.NullableNumber("mapUnitMaxY"), r.Array("overlays").Select((x,i) => ReadOverlay(x, $"{path}.overlays[{i}]", ids)).ToArray(), r.Array("zones").Select((x,i) => ReadZone(x, $"{path}.zones[{i}]", ids)).ToArray());
    }
    private static DistributedMapOverlay ReadOverlay(JsonElement value, string path, IdentityRegistry ids)
    {
        var r = new ObjectReader(value, path, RequireObject); var id = r.Guid("id"); ids.Add(id, "map overlay", path);
        return new DistributedMapOverlay(id, r.String("friendlyName"), r.String("imageAssetKey"), r.Number("scaleXPercent"), r.Number("scaleYPercent"), r.Number("leftOffsetPercent"), r.Number("bottomOffsetPercent"), r.Int("sortOrder"));
    }
    private static DistributedMapZone ReadZone(JsonElement value, string path, IdentityRegistry ids)
    {
        var r = new ObjectReader(value, path, RequireObject); var id = r.Guid("id"); ids.Add(id, "map zone", path);
        return new DistributedMapZone(id, r.String("inGameId"), r.NullableString("friendlyName"), r.NullableNumber("mapUnitMinX"), r.NullableNumber("mapUnitMinY"), r.NullableNumber("mapUnitMaxX"), r.NullableNumber("mapUnitMaxY"), r.Array("scenes").Select((x,i) => ReadScene(x, $"{path}.scenes[{i}]", ids)).ToArray());
    }
    private static DistributedMapScene ReadScene(JsonElement value, string path, IdentityRegistry ids)
    {
        var r = new ObjectReader(value, path, RequireObject); var id = r.Guid("id"); ids.Add(id, "map scene", path);
        return new DistributedMapScene(id, r.String("inGameId"), r.NullableString("friendlyName"), r.NullableString("roomReferenceText"), r.Array("chunks").Select((x,i) => ReadChunk(x, $"{path}.chunks[{i}]", ids)).ToArray());
    }
    private static DistributedMapChunk ReadChunk(JsonElement value, string path, IdentityRegistry ids)
    {
        var r = new ObjectReader(value, path, RequireObject); var id = r.Guid("id"); ids.Add(id, "map chunk", path);
        return new DistributedMapChunk(id, r.Int("cacheIndex"), r.NullableString("initialState"), r.NullableNumber("mapUnitMinX"), r.NullableNumber("mapUnitMinY"), r.NullableNumber("mapUnitMaxX"), r.NullableNumber("mapUnitMaxY"), r.NullableNumber("mapUnitZ"));
    }
    private static DistributedRoomGroupingSnapshot ReadGroups(JsonElement value, string path, IdentityRegistry ids, int version)
    {
        var r = new ObjectReader(value, path, RequireObject);
        return new DistributedRoomGroupingSnapshot(r.Array("groups").Select((x,i) => { var row = new ObjectReader(x, $"{path}.groups[{i}]", RequireObject); var id = row.Guid("id"); ids.Add(id, "room group", row.Path); return new DistributedRoomGroup(id, row.String("friendlyName"), row.NullableString("zoneReferenceText"), version >= 4 && row.Bool("isVirtual"), row.Int("sortOrder"), row.Utc("createdUtc"), row.Utc("updatedUtc")); }).ToArray());
    }
    private static DistributedRoomDocument ReadRoom(JsonElement value, string path, IdentityRegistry ids, int version)
    {
        var r = new ObjectReader(value, path, RequireObject); var id = r.Guid("id"); ids.Add(id, "room", path);
        double minX = 0, minY = 0; double? maxX, maxY, sx, sy, px, py;
        var stale = r.Bool("isSceneImageStale"); var useSceneImage = true;
        if (version == 6)
        {
            minX = r.Number("sceneMinX"); minY = r.Number("sceneMinY");
            maxX = r.NullableNumber("sceneMaxX"); maxY = r.NullableNumber("sceneMaxY");
            sx = r.NullableNumber("sceneImagePixelsPerUnitX"); sy = r.NullableNumber("sceneImagePixelsPerUnitY");
            px = r.NullableNumber("sceneImagePixelOffsetX"); py = r.NullableNumber("sceneImagePixelOffsetY"); useSceneImage = r.Bool("useSceneImage");
        }
        else
        {
            maxX = r.NullableNumber("sceneUnitWidth"); maxY = r.NullableNumber("sceneUnitHeight");
            (sx, sy, px, py, stale) = SceneBounds.ConvertLegacy(maxX, maxY, r.NullableNumber("sceneImageScaleXPercent"),
                r.NullableNumber("sceneImageScaleYPercent"), r.NullableNumber("sceneImagePanXPercent"), r.NullableNumber("sceneImagePanYPercent"), stale);
        }
        return new DistributedRoomDocument(id, r.NullableGuid("roomGroupId"), r.String("referenceId"), r.String("friendlyName"), r.NullableString("inGameId"), r.NullableString("contributors"), r.NullableString("comments"), minX, minY, maxX, maxY, sx, sy, px, py, useSceneImage, stale, r.Int("sortOrder"), r.Bool("isArchived"), r.NullableUtc("archivedUtc"), r.Utc("createdUtc"), r.Utc("updatedUtc"), r.Array("subrooms").Select((x,i) => ReadSubroom(x, $"{path}.subrooms[{i}]", ids, id)).ToArray(), r.Array("transitions").Select((x,i) => ReadTransition(x, $"{path}.transitions[{i}]", ids, id, version)).ToArray(), r.Array("connections").Select((x,i) => ReadConnection(x, $"{path}.connections[{i}]", ids, id)).ToArray(), r.Array("checks").Select((x,i) => ReadCheck(x, $"{path}.checks[{i}]", ids, id, version)).ToArray());
    }
    private static DistributedSubroom ReadSubroom(JsonElement v, string p, IdentityRegistry ids, Guid owner) { var r=new ObjectReader(v,p,RequireObject); var id=r.Guid("id"); ids.AddChild(id,"subroom",p,owner); return new(id,r.String("referenceId"),r.String("friendlyName"),r.NullableString("notes"),r.NullableNumber("sceneUnitX"),r.NullableNumber("sceneUnitY"),r.NullableNumber("sceneUnitWidth"),r.NullableNumber("sceneUnitHeight"),r.Bool("enableAnnotation"),r.Int("sortOrder"),r.Bool("isArchived"),r.NullableUtc("archivedUtc"),r.Utc("createdUtc"),r.Utc("updatedUtc")); }
    private static DistributedTransition ReadTransition(JsonElement v, string p, IdentityRegistry ids, Guid owner, int version) { var r=new ObjectReader(v,p,RequireObject); var id=r.Guid("id"); ids.AddChild(id,"transition",p,owner); return new(id,r.String("alias"),r.String("friendlyName"),r.NullableString("inGameId"),r.NullableNumber("inGamePositionX"),r.NullableNumber("inGamePositionY"),r.NullableNumber("inGamePositionZ"),r.NullableNumber("localPositionX"),r.NullableNumber("localPositionY"),r.NullableNumber("localPositionZ"),r.NullableNumber("annotationSceneUnitX"),r.NullableNumber("annotationSceneUnitY"),r.Bool("enableAnnotation"),r.NullableString("sourceSubroomReferenceText"),r.NullableString("destinationRoomReferenceText"),r.NullableString("destinationTransitionAliasText"),r.String("requirements"),r.String("notes"),r.Int("sortOrder"),r.Bool("isTodo"),r.NullableBool("isVerified"),r.Bool("isArchived"),r.NullableUtc("archivedUtc"),r.Utc("createdUtc"),r.Utc("updatedUtc"),version >= 5 && r.Bool("isUnsafeArrival")); }
    private static DistributedConnection ReadConnection(JsonElement v, string p, IdentityRegistry ids, Guid owner) { var r=new ObjectReader(v,p,RequireObject); var id=r.Guid("id"); ids.AddChild(id,"connection",p,owner); return new(id,r.String("alias"),r.String("friendlyName"),r.String("sourceSubroomReferenceText"),r.String("destinationSubroomReferenceText"),r.String("requirements"),r.String("notes"),r.Bool("enableAnnotation"),r.NullableNumber("sceneUnitX"),r.NullableNumber("sceneUnitY"),r.Int("sortOrder"),r.Bool("isTodo"),r.NullableBool("isVerified"),r.Bool("isArchived"),r.NullableUtc("archivedUtc"),r.Utc("createdUtc"),r.Utc("updatedUtc")); }
    private static DistributedCheck ReadCheck(JsonElement v, string p, IdentityRegistry ids, Guid owner, int version) { var r=new ObjectReader(v,p,RequireObject); var id=r.Guid("id"); ids.AddChild(id,"check",p,owner); string? locationType;if(version==2){_=r.Bool("isIncludedInApworld");locationType=null;}else{locationType=r.NullableString("locationType");if(locationType is not null&&!CheckLocationTypeCatalogue.IsRecognized(locationType))throw new PackageFormatException($"{p}.locationType is not a recognized location type.");}return new(id,r.String("friendlyName"),r.NullableString("inGameId"),r.NullableNumber("inGamePositionX"),r.NullableNumber("inGamePositionY"),r.NullableNumber("inGamePositionZ"),r.NullableNumber("localPositionX"),r.NullableNumber("localPositionY"),r.NullableNumber("localPositionZ"),r.NullableNumber("annotationSceneUnitX"),r.NullableNumber("annotationSceneUnitY"),r.NullableString("subroomReferenceText"),r.String("requirements"),r.String("notes"),locationType,r.Bool("enableAnnotation"),r.Int("sortOrder"),r.Bool("isTodo"),r.NullableBool("isVerified"),r.Bool("isArchived"),r.NullableUtc("archivedUtc"),r.Utc("createdUtc"),r.Utc("updatedUtc")); }

    private static void RejectDuplicateProperties(JsonElement value, string path, HashSet<string> names)
    {
        if (value.ValueKind == JsonValueKind.Object) { names.Clear(); foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw new PackageFormatException($"{path} has duplicate property '{p.Name}'."); RejectDuplicateProperties(p.Value, $"{path}.{p.Name}", new HashSet<string>(StringComparer.Ordinal)); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var (item, index) in value.EnumerateArray().Select((x,i)=>(x,i))) RejectDuplicateProperties(item, $"{path}[{index}]", new HashSet<string>(StringComparer.Ordinal));
    }
    private static void RequireObject(JsonElement e, string path) { if (e.ValueKind != JsonValueKind.Object) throw new PackageFormatException($"{path} must be an object."); }

    private sealed class ObjectReader
    {
        private readonly JsonElement value; public string Path { get; }
        public ObjectReader(JsonElement value, string path, Action<JsonElement,string> ensure) { ensure(value,path); this.value=value; Path=path; }
        public bool Has(string name) => value.TryGetProperty(name, out _);
        public JsonElement? Optional(string name) => value.TryGetProperty(name, out var item) ? item : null;
        private JsonElement Get(string name) => value.TryGetProperty(name,out var item) ? item : throw new PackageFormatException($"{Path}.{name} is required.");
        private JsonElement Kind(string name, JsonValueKind kind) { var item=Get(name); if(item.ValueKind!=kind) throw new PackageFormatException($"{Path}.{name} must be {kind.ToString().ToLowerInvariant()}."); return item; }
        public string String(string n)=>Kind(n,JsonValueKind.String).GetString()!; public string? NullableString(string n){var x=Get(n);if(x.ValueKind==JsonValueKind.Null)return null;if(x.ValueKind!=JsonValueKind.String)throw new PackageFormatException($"{Path}.{n} must be string or null.");return x.GetString();}
        public bool Bool(string n){var x=Get(n);if(x.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new PackageFormatException($"{Path}.{n} must be boolean.");return x.GetBoolean();} public bool? NullableBool(string n){var x=Get(n);return x.ValueKind switch { JsonValueKind.Null=>null,JsonValueKind.True or JsonValueKind.False=>x.GetBoolean(), _=>throw new PackageFormatException($"{Path}.{n} must be boolean or null.")};}
        public int Int(string n){var x=Get(n);if(x.ValueKind!=JsonValueKind.Number||!x.TryGetInt32(out var v))throw new PackageFormatException($"{Path}.{n} must be an integer.");return v;} public double Number(string n){var x=Kind(n,JsonValueKind.Number);if(!x.TryGetDouble(out var v)||!double.IsFinite(v))throw new PackageFormatException($"{Path}.{n} must be a finite number.");return v;} public double? NullableNumber(string n){var x=Get(n);if(x.ValueKind==JsonValueKind.Null)return null;if(x.ValueKind!=JsonValueKind.Number||!x.TryGetDouble(out var v)||!double.IsFinite(v))throw new PackageFormatException($"{Path}.{n} must be finite number or null.");return v;}
        public Guid Guid(string n){var x=String(n);if(!System.Guid.TryParse(x,out var v))throw new PackageFormatException($"{Path}.{n} must be a GUID string.");return v;} public Guid? NullableGuid(string n){var x=Get(n);if(x.ValueKind==JsonValueKind.Null)return null;if(x.ValueKind!=JsonValueKind.String||!System.Guid.TryParse(x.GetString(),out var v))throw new PackageFormatException($"{Path}.{n} must be GUID string or null.");return v;}
        public DateTime Utc(string n)=>ParseUtc(String(n),$"{Path}.{n}"); public DateTime? NullableUtc(string n){var x=Get(n);if(x.ValueKind==JsonValueKind.Null)return null;if(x.ValueKind!=JsonValueKind.String)throw new PackageFormatException($"{Path}.{n} must be UTC timestamp string or null.");return ParseUtc(x.GetString()!,$"{Path}.{n}");} public JsonElement[] Array(string n)=>Kind(n,JsonValueKind.Array).EnumerateArray().ToArray();
    }
    private static DateTime ParseUtc(string text,string path) { if(!text.EndsWith('Z') || !DateTime.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var value) || value.Kind!=DateTimeKind.Utc) throw new PackageFormatException($"{path} must be a UTC timestamp string."); return value; }
    private sealed class IdentityRegistry { private readonly Dictionary<Guid,(string Type,Guid? Owner,string Path)> ids=[]; public void Add(Guid id,string type,string path)=>AddCore(id,type,null,path); public void AddChild(Guid id,string type,string path,Guid owner)=>AddCore(id,type,owner,path); private void AddCore(Guid id,string type,Guid? owner,string path){if(ids.TryGetValue(id,out var previous)){if(previous.Type!=type)throw new PackageFormatException($"{path} reuses GUID {id} for {type}; it is already a {previous.Type} at {previous.Path}.");if(previous.Owner!=owner)throw new PackageFormatException($"{path} reuses {type} GUID {id} under a different room owner.");throw new PackageFormatException($"{path} duplicates {type} GUID {id} from {previous.Path}.");}ids.Add(id,(type,owner,path));} }
    private sealed class PackageFormatException(string message) : Exception(message);
}
