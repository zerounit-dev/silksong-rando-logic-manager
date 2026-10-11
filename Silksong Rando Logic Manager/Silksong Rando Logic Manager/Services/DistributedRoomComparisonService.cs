using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

public static class DistributedRoomComparisonService
{
    public static DistributedRoomComparison Compare(DistributedRoomDocument? current, DistributedRoomDocument incoming, IEnumerable<DistributedRoomDocument> localRooms)
        => CompareWithIdentities(current, incoming, localRooms.Select(x => new DistributedImportRoomIdentity(x.Id, x.FriendlyName, x.InGameId)));

    public static DistributedRoomComparison CompareWithIdentities(DistributedRoomDocument? current, DistributedRoomDocument incoming, IEnumerable<DistributedImportRoomIdentity> localRooms)
    {
        var warnings = localRooms.Where(x => x.Id != incoming.Id && !string.IsNullOrWhiteSpace(incoming.InGameId) && string.Equals(x.InGameId, incoming.InGameId, StringComparison.OrdinalIgnoreCase))
            .Select(x => new DistributedImportWarning("same-game-id", $"{x.FriendlyName} has the same game ID as the incoming room."))
            .ToArray();
        if (current is null) return new(incoming.Id, DistributedRoomComparisonKind.New, null, Fields(null, incoming), Children<DistributedSubroom>(null, incoming.Subrooms), Children<DistributedTransition>(null, incoming.Transitions), Children<DistributedConnection>(null, incoming.Connections), Children<DistributedCheck>(null, incoming.Checks), warnings);
        // An archived local-only child is the durable result of a prior accepted
        // complete-document omission. It is not an unresolved difference on a
        // sessionless retry; an active local-only child still is.
        var subrooms = Children(ActiveOmissionsOnly(current.Subrooms, incoming.Subrooms, x => x.Id, x => x.IsArchived), incoming.Subrooms, current, incoming);
        var transitions = Children(ActiveOmissionsOnly(current.Transitions, incoming.Transitions, x => x.Id, x => x.IsArchived), incoming.Transitions, current, incoming);
        var connections = Children(ActiveOmissionsOnly(current.Connections, incoming.Connections, x => x.Id, x => x.IsArchived), incoming.Connections, current, incoming);
        var checks = Children(ActiveOmissionsOnly(current.Checks, incoming.Checks, x => x.Id, x => x.IsArchived), incoming.Checks, current, incoming);
        var fields = Fields(current, incoming);
        var identical = fields.All(x => x.Evidence == DistributedComparisonEvidence.Neutral) && subrooms.All(IsNeutral) && transitions.All(IsNeutral) && connections.All(IsNeutral) && checks.All(IsNeutral);
        var summary = new DistributedRoomComparisonSummary(
            incoming.UpdatedUtc.CompareTo(current.UpdatedUtc) switch { > 0 => DistributedUpdatedUtcComparison.Newer, < 0 => DistributedUpdatedUtcComparison.Older, _ => DistributedUpdatedUtcComparison.Same },
            fields.Count(x => x.Evidence == DistributedComparisonEvidence.Changed),
            subrooms.Count(x => x.Evidence != DistributedComparisonEvidence.Neutral),
            transitions.Count(x => x.Evidence != DistributedComparisonEvidence.Neutral),
            connections.Count(x => x.Evidence != DistributedComparisonEvidence.Neutral),
            checks.Count(x => x.Evidence != DistributedComparisonEvidence.Neutral));
        return new(incoming.Id, identical ? DistributedRoomComparisonKind.Identical : DistributedRoomComparisonKind.Changed, summary, fields, subrooms, transitions, connections, checks, warnings);
    }

    private static bool IsNeutral<T>(DistributedChildComparison<T> row) => row.Evidence == DistributedComparisonEvidence.Neutral;
    private static IReadOnlyList<T> ActiveOmissionsOnly<T>(IReadOnlyList<T> current, IReadOnlyList<T> incoming, Func<T, Guid> id, Func<T, bool> archived)
        => current.Where(x => !archived(x) || incoming.Any(y => id(y) == id(x))).ToArray();
    private static IReadOnlyList<DistributedChildComparison<T>> Children<T>(IReadOnlyList<T>? current, IReadOnlyList<T> incoming, DistributedRoomDocument? currentRoom = null, DistributedRoomDocument? incomingRoom = null) where T : class
    {
        var currentById = (current ?? []).ToDictionary(Id);
        var incomingById = incoming.ToDictionary(Id);
        return currentById.Keys.Union(incomingById.Keys).OrderBy(x => x).Select(id =>
        {
            var hasCurrent = currentById.TryGetValue(id, out var oldValue); var hasIncoming = incomingById.TryGetValue(id, out var newValue);
            var fields = ChildFields(hasCurrent ? oldValue : default, hasIncoming ? newValue : default, currentRoom, incomingRoom);
            var evidence = hasCurrent && hasIncoming
                ? fields.All(x => x.Evidence == DistributedComparisonEvidence.Neutral) ? DistributedComparisonEvidence.Neutral : DistributedComparisonEvidence.Changed
                : hasCurrent ? DistributedComparisonEvidence.Current : DistributedComparisonEvidence.Incoming;
            return new DistributedChildComparison<T>(id, evidence, fields);
        }).ToArray();
    }
    private static Guid Id<T>(T value) => value switch { DistributedSubroom x => x.Id, DistributedTransition x => x.Id, DistributedConnection x => x.Id, DistributedCheck x => x.Id, _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static IReadOnlyList<DistributedComparisonValue> Fields(DistributedRoomDocument? current, DistributedRoomDocument incoming)
    {
        var values = new (string Name, object? Current, object? Incoming)[]
        {
            ("id", current?.Id, incoming.Id), ("roomGroupId", current?.RoomGroupId, incoming.RoomGroupId), ("referenceId", current?.ReferenceId, incoming.ReferenceId), ("friendlyName", current?.FriendlyName, incoming.FriendlyName), ("inGameId", current?.InGameId, incoming.InGameId), ("contributors", current?.Contributors, incoming.Contributors), ("comments", current?.Comments, incoming.Comments),
            ("sceneMinX", current?.SceneMinX, incoming.SceneMinX), ("sceneMinY", current?.SceneMinY, incoming.SceneMinY), ("sceneMaxX", current?.SceneMaxX, incoming.SceneMaxX), ("sceneMaxY", current?.SceneMaxY, incoming.SceneMaxY),
            ("sceneImagePixelsPerUnitX", current?.SceneImagePixelsPerUnitX, incoming.SceneImagePixelsPerUnitX), ("sceneImagePixelsPerUnitY", current?.SceneImagePixelsPerUnitY, incoming.SceneImagePixelsPerUnitY),
            ("sceneImagePixelOffsetX", current?.SceneImagePixelOffsetX, incoming.SceneImagePixelOffsetX), ("sceneImagePixelOffsetY", current?.SceneImagePixelOffsetY, incoming.SceneImagePixelOffsetY), ("useSceneImage", current?.UseSceneImage, incoming.UseSceneImage),
            ("isSceneImageStale", current?.IsSceneImageStale, incoming.IsSceneImageStale), ("sortOrder", current?.SortOrder, incoming.SortOrder), ("isArchived", current?.IsArchived, incoming.IsArchived), ("archivedUtc", current?.ArchivedUtc, incoming.ArchivedUtc), ("createdUtc", current?.CreatedUtc, incoming.CreatedUtc), ("updatedUtc", current?.UpdatedUtc, incoming.UpdatedUtc)
        };
        var exact = current is null ? new HashSet<string>() : ExactRoomFields(current, incoming);
        return values.Select(x => current is null ? new DistributedComparisonValue(x.Name, null, Text(x.Incoming), DistributedComparisonEvidence.Incoming) : Matches(x.Name, x.Current, x.Incoming, exact) ? new DistributedComparisonValue(x.Name, Text(x.Current), Text(x.Incoming), DistributedComparisonEvidence.Neutral) : new DistributedComparisonValue(x.Name, Text(x.Current), Text(x.Incoming), DistributedComparisonEvidence.Changed)).ToArray();
    }
    private static IReadOnlyList<DistributedComparisonValue> ChildFields<T>(T? current, T? incoming, DistributedRoomDocument? currentRoom, DistributedRoomDocument? incomingRoom) where T : class
    {
        // For one-sided rows, use the present record on both sides solely to
        // establish the complete exchanged-field column set; evidence below
        // then exposes values only on the present side.
        var currentForFields = current ?? incoming ?? throw new ArgumentOutOfRangeException(nameof(current));
        var incomingForFields = incoming ?? current;
        IEnumerable<(string, object?, object?)> values = (currentForFields, incomingForFields) switch
        {
            (DistributedSubroom c, DistributedSubroom i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("referenceId", c.ReferenceId, i.ReferenceId), ("friendlyName", c.FriendlyName, i.FriendlyName), ("notes", c.Notes, i.Notes), ("sceneUnitX", c.SceneUnitX, i.SceneUnitX), ("sceneUnitY", c.SceneUnitY, i.SceneUnitY), ("sceneUnitWidth", c.SceneUnitWidth, i.SceneUnitWidth), ("sceneUnitHeight", c.SceneUnitHeight, i.SceneUnitHeight), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sortOrder", c.SortOrder, i.SortOrder), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            (DistributedTransition c, DistributedTransition i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("alias", c.Alias, i.Alias), ("friendlyName", c.FriendlyName, i.FriendlyName), ("inGameId", c.InGameId, i.InGameId), ("inGamePositionX", c.InGamePositionX, i.InGamePositionX), ("inGamePositionY", c.InGamePositionY, i.InGamePositionY), ("inGamePositionZ", c.InGamePositionZ, i.InGamePositionZ), ("localPositionX", c.LocalPositionX, i.LocalPositionX), ("localPositionY", c.LocalPositionY, i.LocalPositionY), ("localPositionZ", c.LocalPositionZ, i.LocalPositionZ), ("annotationSceneUnitX", c.AnnotationSceneUnitX, i.AnnotationSceneUnitX), ("annotationSceneUnitY", c.AnnotationSceneUnitY, i.AnnotationSceneUnitY), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sourceSubroomReferenceText", c.SourceSubroomReferenceText, i.SourceSubroomReferenceText), ("destinationRoomReferenceText", c.DestinationRoomReferenceText, i.DestinationRoomReferenceText), ("destinationTransitionAliasText", c.DestinationTransitionAliasText, i.DestinationTransitionAliasText), ("requirements", c.Requirements, i.Requirements), ("notes", c.Notes, i.Notes), ("sortOrder", c.SortOrder, i.SortOrder), ("isTodo", c.IsTodo, i.IsTodo), ("isVerified", c.IsVerified, i.IsVerified), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc), ("isUnsafeArrival", c.IsUnsafeArrival, i.IsUnsafeArrival) },
            (DistributedConnection c, DistributedConnection i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("alias", c.Alias, i.Alias), ("friendlyName", c.FriendlyName, i.FriendlyName), ("sourceSubroomReferenceText", c.SourceSubroomReferenceText, i.SourceSubroomReferenceText), ("destinationSubroomReferenceText", c.DestinationSubroomReferenceText, i.DestinationSubroomReferenceText), ("requirements", c.Requirements, i.Requirements), ("notes", c.Notes, i.Notes), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sceneUnitX", c.SceneUnitX, i.SceneUnitX), ("sceneUnitY", c.SceneUnitY, i.SceneUnitY), ("sortOrder", c.SortOrder, i.SortOrder), ("isTodo", c.IsTodo, i.IsTodo), ("isVerified", c.IsVerified, i.IsVerified), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            (DistributedCheck c, DistributedCheck i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("friendlyName", c.FriendlyName, i.FriendlyName), ("inGameId", c.InGameId, i.InGameId), ("inGamePositionX", c.InGamePositionX, i.InGamePositionX), ("inGamePositionY", c.InGamePositionY, i.InGamePositionY), ("inGamePositionZ", c.InGamePositionZ, i.InGamePositionZ), ("localPositionX", c.LocalPositionX, i.LocalPositionX), ("localPositionY", c.LocalPositionY, i.LocalPositionY), ("localPositionZ", c.LocalPositionZ, i.LocalPositionZ), ("annotationSceneUnitX", c.AnnotationSceneUnitX, i.AnnotationSceneUnitX), ("annotationSceneUnitY", c.AnnotationSceneUnitY, i.AnnotationSceneUnitY), ("subroomReferenceText", c.SubroomReferenceText, i.SubroomReferenceText), ("requirements", c.Requirements, i.Requirements), ("notes", c.Notes, i.Notes), ("locationType", c.LocationType, i.LocationType), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sortOrder", c.SortOrder, i.SortOrder), ("isTodo", c.IsTodo, i.IsTodo), ("isVerified", c.IsVerified, i.IsVerified), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            _ => throw new ArgumentOutOfRangeException(nameof(current))
        };
        var currentOnly = current is not null && incoming is null;
        var incomingOnly = current is null && incoming is not null;
        var fields = values.ToArray();
        var exact = ExactChildFields(fields, currentRoom, incomingRoom);
        return fields.Select(x => currentOnly ? new DistributedComparisonValue(x.Item1, Text(x.Item2), null, DistributedComparisonEvidence.Current) : incomingOnly ? new DistributedComparisonValue(x.Item1, null, Text(x.Item3), DistributedComparisonEvidence.Incoming) : Matches(x.Item1, x.Item2, x.Item3, exact) ? new DistributedComparisonValue(x.Item1, Text(x.Item2), Text(x.Item3), DistributedComparisonEvidence.Neutral) : new DistributedComparisonValue(x.Item1, Text(x.Item2), Text(x.Item3), DistributedComparisonEvidence.Changed)).ToArray();
    }

    // Evidence keeps both unmodified serialized sides, including tolerated values:
    // SameComparison's exact pre-write guard must still detect tiny local edits.
    private static bool Matches(string name, object? current, object? incoming, IReadOnlySet<string> exact)
    {
        if (Equals(current, incoming)) return true;
        if (exact.Contains(name) || current is not double c || incoming is not double i || !double.IsFinite(c) || !double.IsFinite(i)) return false;
        var tolerance = name switch
        {
            "sceneImagePixelsPerUnitX" or "sceneImagePixelsPerUnitY" => 0.0001,
            "inGamePositionX" or "inGamePositionY" or "inGamePositionZ" or "localPositionX" or "localPositionY" or "localPositionZ" => 0.001,
            "sceneMinX" or "sceneMinY" or "sceneMaxX" or "sceneMaxY" or "sceneUnitX" or "sceneUnitY" or "sceneUnitWidth" or "sceneUnitHeight" or "annotationSceneUnitX" or "annotationSceneUnitY" or "sceneImagePixelOffsetX" or "sceneImagePixelOffsetY" => 0.01,
            _ => 0d
        };
        var difference = Math.Abs(c - i);
        return double.IsFinite(difference) && difference <= tolerance;
    }

    private static bool UsableBounds(DistributedRoomDocument room) => SceneBounds.TryGetSize(room.SceneMinX, room.SceneMinY, room.SceneMaxX, room.SceneMaxY, out _, out _);
    private static bool Pair(double? x, double? y) => x is { } px && y is { } py && double.IsFinite(px) && double.IsFinite(py);
    private static bool Rectangle(double? x, double? y, double? w, double? h) => Pair(x, y) && Pair(w, h) && w > 0 && h > 0 && double.IsFinite(x!.Value + w!.Value) && double.IsFinite(y!.Value + h!.Value);

    // Row-local safety mirrors geometry point status only, not graph admission,
    // alias repair or generation. Archived/disabled supplied points participate.
    private static bool? OutOfBounds(DistributedRoomDocument room, double? x, double? y)
    {
        if (!UsableBounds(room) || !Pair(x, y)) return null;
        var dx = x!.Value < room.SceneMinX ? room.SceneMinX - x.Value : x.Value > room.SceneMaxX!.Value ? x.Value - room.SceneMaxX.Value : 0;
        var dy = y!.Value < room.SceneMinY ? room.SceneMinY - y.Value : y.Value > room.SceneMaxY!.Value ? y.Value - room.SceneMaxY.Value : 0;
        return dx >= 1 || dy >= 1 || Math.Sqrt(dx * dx + dy * dy) >= 1;
    }

    private static IEnumerable<(double? X, double? Y)> Points(DistributedRoomDocument room) =>
        room.Transitions.Select(x => (x.InGamePositionX, x.InGamePositionY))
            .Concat(room.Checks.Select(x => (x.InGamePositionX, x.InGamePositionY)))
            .Concat(room.Connections.Select(x => (x.SceneUnitX, x.SceneUnitY)));

    private static HashSet<string> ExactRoomFields(DistributedRoomDocument current, DistributedRoomDocument incoming)
    {
        var exact = new HashSet<string>();
        if (UsableBounds(current) != UsableBounds(incoming) || Points(current).Concat(Points(incoming)).Any(p => OutOfBounds(current, p.X, p.Y) != OutOfBounds(incoming, p.X, p.Y)))
            exact.UnionWith(["sceneMinX", "sceneMinY", "sceneMaxX", "sceneMaxY"]);
        if (SceneBounds.HasUsableScales(current.SceneImagePixelsPerUnitX, current.SceneImagePixelsPerUnitY) != SceneBounds.HasUsableScales(incoming.SceneImagePixelsPerUnitX, incoming.SceneImagePixelsPerUnitY))
            exact.UnionWith(["sceneImagePixelsPerUnitX", "sceneImagePixelsPerUnitY"]);
        if (Pair(current.SceneImagePixelOffsetX, current.SceneImagePixelOffsetY) != Pair(incoming.SceneImagePixelOffsetX, incoming.SceneImagePixelOffsetY))
            exact.UnionWith(["sceneImagePixelOffsetX", "sceneImagePixelOffsetY"]);
        return exact;
    }

    private static HashSet<string> ExactChildFields((string Name, object? Current, object? Incoming)[] fields, DistributedRoomDocument? currentRoom, DistributedRoomDocument? incomingRoom)
    {
        var exact = new HashSet<string>();
        var byName = fields.ToDictionary(x => x.Name);
        double? Value(string name, bool incoming) => (double?)(incoming ? byName[name].Incoming : byName[name].Current);
        void ProtectPair(string x, string y, bool boundsStatus)
        {
            if (!byName.ContainsKey(x)) return;
            var cx = Value(x, false); var cy = Value(y, false);
            var ix = Value(x, true); var iy = Value(y, true);
            if (Pair(cx, cy) != Pair(ix, iy) || boundsStatus &&
                ((currentRoom is not null && OutOfBounds(currentRoom, cx, cy) != OutOfBounds(currentRoom, ix, iy)) ||
                 (incomingRoom is not null && OutOfBounds(incomingRoom, cx, cy) != OutOfBounds(incomingRoom, ix, iy))))
                exact.UnionWith([x, y]);
        }
        if (byName.ContainsKey("sceneUnitWidth"))
        {
            if (Rectangle(Value("sceneUnitX", false), Value("sceneUnitY", false), Value("sceneUnitWidth", false), Value("sceneUnitHeight", false)) !=
                Rectangle(Value("sceneUnitX", true), Value("sceneUnitY", true), Value("sceneUnitWidth", true), Value("sceneUnitHeight", true)))
                exact.UnionWith(["sceneUnitX", "sceneUnitY", "sceneUnitWidth", "sceneUnitHeight"]);
        }
        else ProtectPair("sceneUnitX", "sceneUnitY", true);
        ProtectPair("annotationSceneUnitX", "annotationSceneUnitY", false);
        ProtectPair("inGamePositionX", "inGamePositionY", true);
        ProtectPair("localPositionX", "localPositionY", false);
        return exact;
    }
    private static string? Text(object? value) => value is null ? null : JsonSerializer.Serialize(value);
}
