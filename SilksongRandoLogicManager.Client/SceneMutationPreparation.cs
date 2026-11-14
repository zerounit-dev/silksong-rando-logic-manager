using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Client;

internal static class SceneMutationPreparation
{
    public static SceneApplyRequest Prepare(SceneDumpReview review, SceneReviewEvidence evidence, SceneWorkbenchCommand command)
    {
        var nodes = SceneTransport.Flatten(review.RootObjects).Where(n => SceneDumpIdentity.IsImportable(n.Classification)).ToArray();
        if (command.Rows.Count != nodes.Length || command.Rows.Select(r => r.Instruction.ObjectId).Distinct().Count() != nodes.Length)
            throw Invalid("Incoming rows must be current transitions, checks or enemies.");
        var room = command.TargetRoomId is { } id ? evidence.Preview.MatchingRooms.SingleOrDefault(r => r.Id == id)
            : evidence.Preview.MatchingRooms.Count == 1 ? evidence.Preview.MatchingRooms[0] : null;
        if (command.TargetRoomId is not null && room is null)
            throw Invalid("The selected target room is no longer an active scene-name match.");
        if (room is null && (evidence.Preview.MatchingRooms.Count != 0 || string.IsNullOrWhiteSpace(review.SceneName)))
            throw Invalid("Select one of the matching active rooms before importing. A blank scene identity cannot create a room.");
        var assigned = command.Rows.Where(r => r.Instruction.ExistingRecordId is not null).Select(r => r.Instruction.ExistingRecordId!.Value).ToHashSet();
        if (assigned.Count != command.Rows.Count(r => r.Instruction.ExistingRecordId is not null))
            throw Invalid("An existing target is assigned more than once. Correct both incoming rows, including unchecked Apply rows.");
        for (var i = 0; i < nodes.Length; i++)
        {
            var r = command.Rows[i]; var instruction = r.Instruction; var node = nodes[i];
            if (instruction.ObjectId != node.Id || instruction.FriendlyName is null || r.Requirements is null || r.Notes is null ||
                !Enum.IsDefined(instruction.Mode) || r.Unresolved && instruction.ExistingRecordId is not null)
                throw Invalid("Incoming rows are malformed.");
            if (instruction.ExistingRecordId is { } target)
            {
                var records = node.Classification == SceneDumpClassification.Exit ? evidence.Preview.Transitions : evidence.Preview.Checks;
                if (instruction.Mode != SceneImportMode.Update || !records.Any(t => t.Id == target && t.RoomId == room?.Id &&
                    (node.Classification == SceneDumpClassification.Exit || t.IsEnemy == (node.Classification == SceneDumpClassification.Enemy))))
                    throw Invalid("A selected target is no longer active in this room/category. Choose another target.");
            }
            else if (instruction.Mode != SceneImportMode.Create) throw Invalid("Choose an existing target or Create new.");
        }
        if (command.Archives.Select(a => a.RecordId).Distinct().Count() != command.Archives.Count)
            throw Invalid("Each archive target must appear only once.");
        foreach (var archive in command.Archives)
        {
            if (!SceneDumpIdentity.IsImportable(archive.Classification)) throw Invalid("Archive targets must be transitions, checks or enemies.");
            var records = archive.Classification == SceneDumpClassification.Exit ? evidence.Preview.Transitions : evidence.Preview.Checks;
            if (!records.Any(t => t.Id == archive.RecordId && t.RoomId == room?.Id &&
                (archive.Classification == SceneDumpClassification.Exit || t.IsEnemy == (archive.Classification == SceneDumpClassification.Enemy))))
                throw Invalid("An archive target is no longer active in this room/category.");
        }
        var archives = command.Archives.Where(a => !assigned.Contains(a.RecordId)).Select(a => new SceneArchiveMutation(a.RecordId,
            a.Classification switch { SceneDumpClassification.Exit => SceneMutationFamily.Transition,
                SceneDumpClassification.Check => SceneMutationFamily.Check, SceneDumpClassification.Enemy => SceneMutationFamily.Enemy,
                _ => throw Invalid("Archive targets must be transitions, checks or enemies.") })).ToArray();
        // This is a local simulation of the retained importer's evolving active list,
        // after archives and before each source-ordered selected transition instruction.
        var destinationRooms = evidence.Destinations.Rooms.ToList();
        var sourceId = room?.Id ?? Guid.Empty; // Local intent identity only, never a persisted ID claim.
        if (room is null) destinationRooms.Add(new(sourceId, review.SceneName, ""));
        var transitions = evidence.Destinations.Transitions.Where(t => !archives.Any(a => a.Family == SceneMutationFamily.Transition && a.Id == t.Id)).ToList();
        var creates = new List<SceneTransitionCreate>(); var updates = new List<SceneTransitionUpdate>();
        var checks = new List<SceneCheckCreate>(); var checkUpdates = new List<SceneCheckUpdate>(); var enemyUpdates = new List<SceneEnemyUpdate>();
        for (var i = 0; i < nodes.Length; i++)
        {
            var row = command.Rows[i]; if (!row.Apply || row.Unresolved) continue;
            var node = nodes[i]; var instruction = row.Instruction;
            var metadata = new SceneMetadata(SceneDumpIdentity.GameId(node), node.WorldPosition?.X, node.WorldPosition?.Y, node.WorldPosition?.Z,
                node.LocalPosition?.X, node.LocalPosition?.Y, node.LocalPosition?.Z);
            if (instruction.Mode == SceneImportMode.Update)
            {
                var target = instruction.ExistingRecordId!.Value;
                if (node.Classification == SceneDumpClassification.Exit)
                {
                    updates.Add(new(target, metadata));
                    var index = transitions.FindIndex(t => t.Id == target);
                    if (index >= 0) transitions[index] = transitions[index] with { InGameId = metadata.InGameId };
                }
                else if (node.Classification == SceneDumpClassification.Enemy) enemyUpdates.Add(new(target, metadata, row.Requirements, row.Notes));
                else checkUpdates.Add(new(target, metadata));
                continue;
            }
            var pair = metadata.X is { } x && metadata.Y is { } y && double.IsFinite(x) && double.IsFinite(y);
            var annotation = new SceneInitialAnnotation(pair ? metadata.X : null, pair ? metadata.Y : null, true);
            if (node.Classification == SceneDumpClassification.Exit)
            {
                var point = node.Components.SingleOrDefault(c => c.Type == "TransitionPoint");
                var rooms = point is null ? [] : destinationRooms.Where(r => Matches(r.InGameId, point.TargetScene)).ToArray();
                var entries = rooms.Length == 1 ? transitions.Where(t => t.RoomId == rooms[0].Id && Matches(t.InGameId, point!.EntryPoint)).ToArray() : [];
                var matched = rooms.Length == 1 && entries.Length == 1;
                creates.Add(new(instruction.FriendlyName, metadata, annotation, node.IsAct3Only ? "Act 3" : "",
                    new(matched && rooms[0].Id != Guid.Empty ? rooms[0].ReferenceId : null,
                        matched && rooms[0].Id == Guid.Empty, matched ? entries[0].Alias : null)));
                transitions.Add(new(node.Id, sourceId, metadata.InGameId, ""));
            }
            else checks.Add(new(instruction.FriendlyName, metadata, annotation,
                node.Classification == SceneDumpClassification.Enemy ? row.Requirements : node.IsAct3Only ? "Act 3" : "",
                node.Classification == SceneDumpClassification.Enemy ? row.Notes : "", node.Classification == SceneDumpClassification.Enemy ? "enemy" : null));
        }
        SceneBoundsMutation? bounds = null;
        if (command.ApplyBounds && review.SceneUnitSize is { } size)
        {
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                throw Invalid("Scene dimensions require a valid parsed scene size.");
            var original = new SceneBoundsObservation(room?.SceneMinX ?? 0, room?.SceneMinY ?? 0, room?.SceneMaxX, room?.SceneMaxY);
            if (original.MaxX is null && original.MaxY is null)
                bounds = new(SceneBoundsMutationKind.Initialize, original, size.Width, size.Height);
            else if (command.ReplacementDimensions is { } replacement)
            {
                if (original.MaxX is not { } mx || original.MaxY is not { } my || !double.IsFinite(mx) || !double.IsFinite(my) || mx <= 0 || my <= 0 || mx == size.Width && my == size.Height)
                    throw Invalid("Scene dimensions can only be replaced when complete current dimensions differ from the parsed size.");
                bounds = new(SceneBoundsMutationKind.Replace, original, replacement.Width, replacement.Height);
            }
            if (bounds is not null && (!double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxY) || bounds.MaxX <= 0 || bounds.MaxY <= 0 ||
                !SceneBounds.TryGetSize(original.MinX, original.MinY, bounds.MaxX, bounds.MaxY, out _, out _)))
                throw Invalid("Imported scene maxima must exceed the retained minima and produce finite positive dimensions.");
        }
        else if (command.ReplacementDimensions is not null) throw Invalid("Scene dimensions can only be replaced from a valid parsed scene size.");
        return new(review.SceneName, evidence.Fingerprint, new(room?.Id, room is null ? review.SceneName : null, room is null ? review.SceneName : null),
            evidence.Destinations.Keys, evidence.Destinations.Fingerprint, creates, updates, checks, checkUpdates, enemyUpdates, archives, bounds);
    }
    private static bool Matches(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static SceneWorkbenchValidationException Invalid(string message) => new(message);
}
