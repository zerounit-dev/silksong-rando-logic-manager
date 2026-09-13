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
        var subrooms = Children(ActiveOmissionsOnly(current.Subrooms, incoming.Subrooms, x => x.Id, x => x.IsArchived), incoming.Subrooms);
        var transitions = Children(ActiveOmissionsOnly(current.Transitions, incoming.Transitions, x => x.Id, x => x.IsArchived), incoming.Transitions);
        var connections = Children(ActiveOmissionsOnly(current.Connections, incoming.Connections, x => x.Id, x => x.IsArchived), incoming.Connections);
        var checks = Children(ActiveOmissionsOnly(current.Checks, incoming.Checks, x => x.Id, x => x.IsArchived), incoming.Checks);
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
    private static IReadOnlyList<DistributedChildComparison<T>> Children<T>(IReadOnlyList<T>? current, IReadOnlyList<T> incoming) where T : class
    {
        var currentById = (current ?? []).ToDictionary(Id);
        var incomingById = incoming.ToDictionary(Id);
        return currentById.Keys.Union(incomingById.Keys).OrderBy(x => x).Select(id =>
        {
            var hasCurrent = currentById.TryGetValue(id, out var oldValue); var hasIncoming = incomingById.TryGetValue(id, out var newValue);
            var fields = ChildFields(hasCurrent ? oldValue : default, hasIncoming ? newValue : default);
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
            ("id", current?.Id, incoming.Id), ("roomGroupId", current?.RoomGroupId, incoming.RoomGroupId), ("referenceId", current?.ReferenceId, incoming.ReferenceId), ("friendlyName", current?.FriendlyName, incoming.FriendlyName), ("inGameId", current?.InGameId, incoming.InGameId), ("contributors", current?.Contributors, incoming.Contributors), ("comments", current?.Comments, incoming.Comments), ("sceneUnitWidth", current?.SceneUnitWidth, incoming.SceneUnitWidth), ("sceneUnitHeight", current?.SceneUnitHeight, incoming.SceneUnitHeight), ("sceneImageScaleXPercent", current?.SceneImageScaleXPercent, incoming.SceneImageScaleXPercent), ("sceneImageScaleYPercent", current?.SceneImageScaleYPercent, incoming.SceneImageScaleYPercent), ("sceneImagePanXPercent", current?.SceneImagePanXPercent, incoming.SceneImagePanXPercent), ("sceneImagePanYPercent", current?.SceneImagePanYPercent, incoming.SceneImagePanYPercent), ("isSceneImageStale", current?.IsSceneImageStale, incoming.IsSceneImageStale), ("sortOrder", current?.SortOrder, incoming.SortOrder), ("isArchived", current?.IsArchived, incoming.IsArchived), ("archivedUtc", current?.ArchivedUtc, incoming.ArchivedUtc), ("createdUtc", current?.CreatedUtc, incoming.CreatedUtc), ("updatedUtc", current?.UpdatedUtc, incoming.UpdatedUtc)
        };
        return values.Select(x => current is null ? new DistributedComparisonValue(x.Name, null, Text(x.Incoming), DistributedComparisonEvidence.Incoming) : Equals(x.Current, x.Incoming) ? new DistributedComparisonValue(x.Name, Text(x.Current), Text(x.Incoming), DistributedComparisonEvidence.Neutral) : new DistributedComparisonValue(x.Name, Text(x.Current), Text(x.Incoming), DistributedComparisonEvidence.Changed)).ToArray();
    }
    private static IReadOnlyList<DistributedComparisonValue> ChildFields<T>(T? current, T? incoming) where T : class
    {
        // For one-sided rows, use the present record on both sides solely to
        // establish the complete exchanged-field column set; evidence below
        // then exposes values only on the present side.
        var currentForFields = current ?? incoming ?? throw new ArgumentOutOfRangeException(nameof(current));
        var incomingForFields = incoming ?? current;
        IEnumerable<(string, object?, object?)> values = (currentForFields, incomingForFields) switch
        {
            (DistributedSubroom c, DistributedSubroom i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("referenceId", c.ReferenceId, i.ReferenceId), ("friendlyName", c.FriendlyName, i.FriendlyName), ("notes", c.Notes, i.Notes), ("sceneUnitX", c.SceneUnitX, i.SceneUnitX), ("sceneUnitY", c.SceneUnitY, i.SceneUnitY), ("sceneUnitWidth", c.SceneUnitWidth, i.SceneUnitWidth), ("sceneUnitHeight", c.SceneUnitHeight, i.SceneUnitHeight), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sortOrder", c.SortOrder, i.SortOrder), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            (DistributedTransition c, DistributedTransition i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("alias", c.Alias, i.Alias), ("friendlyName", c.FriendlyName, i.FriendlyName), ("inGameId", c.InGameId, i.InGameId), ("inGamePositionX", c.InGamePositionX, i.InGamePositionX), ("inGamePositionY", c.InGamePositionY, i.InGamePositionY), ("inGamePositionZ", c.InGamePositionZ, i.InGamePositionZ), ("localPositionX", c.LocalPositionX, i.LocalPositionX), ("localPositionY", c.LocalPositionY, i.LocalPositionY), ("localPositionZ", c.LocalPositionZ, i.LocalPositionZ), ("annotationSceneUnitX", c.AnnotationSceneUnitX, i.AnnotationSceneUnitX), ("annotationSceneUnitY", c.AnnotationSceneUnitY, i.AnnotationSceneUnitY), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sourceSubroomReferenceText", c.SourceSubroomReferenceText, i.SourceSubroomReferenceText), ("destinationRoomReferenceText", c.DestinationRoomReferenceText, i.DestinationRoomReferenceText), ("destinationTransitionAliasText", c.DestinationTransitionAliasText, i.DestinationTransitionAliasText), ("requirements", c.Requirements, i.Requirements), ("notes", c.Notes, i.Notes), ("sortOrder", c.SortOrder, i.SortOrder), ("isTodo", c.IsTodo, i.IsTodo), ("isVerified", c.IsVerified, i.IsVerified), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            (DistributedConnection c, DistributedConnection i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("alias", c.Alias, i.Alias), ("friendlyName", c.FriendlyName, i.FriendlyName), ("sourceSubroomReferenceText", c.SourceSubroomReferenceText, i.SourceSubroomReferenceText), ("destinationSubroomReferenceText", c.DestinationSubroomReferenceText, i.DestinationSubroomReferenceText), ("requirements", c.Requirements, i.Requirements), ("notes", c.Notes, i.Notes), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sceneUnitX", c.SceneUnitX, i.SceneUnitX), ("sceneUnitY", c.SceneUnitY, i.SceneUnitY), ("sortOrder", c.SortOrder, i.SortOrder), ("isTodo", c.IsTodo, i.IsTodo), ("isVerified", c.IsVerified, i.IsVerified), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            (DistributedCheck c, DistributedCheck i) => new (string, object?, object?)[] { ("id", c.Id, i.Id), ("friendlyName", c.FriendlyName, i.FriendlyName), ("inGameId", c.InGameId, i.InGameId), ("inGamePositionX", c.InGamePositionX, i.InGamePositionX), ("inGamePositionY", c.InGamePositionY, i.InGamePositionY), ("inGamePositionZ", c.InGamePositionZ, i.InGamePositionZ), ("localPositionX", c.LocalPositionX, i.LocalPositionX), ("localPositionY", c.LocalPositionY, i.LocalPositionY), ("localPositionZ", c.LocalPositionZ, i.LocalPositionZ), ("annotationSceneUnitX", c.AnnotationSceneUnitX, i.AnnotationSceneUnitX), ("annotationSceneUnitY", c.AnnotationSceneUnitY, i.AnnotationSceneUnitY), ("subroomReferenceText", c.SubroomReferenceText, i.SubroomReferenceText), ("requirements", c.Requirements, i.Requirements), ("notes", c.Notes, i.Notes), ("locationType", c.LocationType, i.LocationType), ("enableAnnotation", c.EnableAnnotation, i.EnableAnnotation), ("sortOrder", c.SortOrder, i.SortOrder), ("isTodo", c.IsTodo, i.IsTodo), ("isVerified", c.IsVerified, i.IsVerified), ("isArchived", c.IsArchived, i.IsArchived), ("archivedUtc", c.ArchivedUtc, i.ArchivedUtc), ("createdUtc", c.CreatedUtc, i.CreatedUtc), ("updatedUtc", c.UpdatedUtc, i.UpdatedUtc) },
            _ => throw new ArgumentOutOfRangeException(nameof(current))
        };
        var currentOnly = current is not null && incoming is null;
        var incomingOnly = current is null && incoming is not null;
        return values.Select(x => currentOnly ? new DistributedComparisonValue(x.Item1, Text(x.Item2), null, DistributedComparisonEvidence.Current) : incomingOnly ? new DistributedComparisonValue(x.Item1, null, Text(x.Item3), DistributedComparisonEvidence.Incoming) : Equals(x.Item2, x.Item3) ? new DistributedComparisonValue(x.Item1, Text(x.Item2), Text(x.Item3), DistributedComparisonEvidence.Neutral) : new DistributedComparisonValue(x.Item1, Text(x.Item2), Text(x.Item3), DistributedComparisonEvidence.Changed)).ToArray();
    }
    private static string? Text(object? value) => value is null ? null : JsonSerializer.Serialize(value);
}
