using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.RoomEditing;

public sealed partial class RoomWorkspace
{
    // Rebuild from confirmed facts, then replay ONLY each immutable operation's
    // intended differences. No projected result ever becomes another intent's origin.
    // Live values stay outside these rows and are overlaid at native installation.
    private Installation Stage(RoomConfirmedState confirmed, ImmutableArray<RoomQueuedOperation> pending, ImmutableArray<RoomNativeField> native, RoomBlockedHead? blocked,
        IReadOnlyDictionary<Guid, RoomTableRow>? restored = null)
    {
        restored ??= revertedCreations;
        var document = confirmed.Document;
        if (document is null) return Materialize(Header, []);
        var blockedIntent = blocked is null ? null : pending.SingleOrDefault(x => x.OperationId == blocked.OperationId);
        // A blocked command keeps its draft and may leave an older confirmed
        // snapshot in place. Missing evidence only withdraws the named receiver's action.
        var blockedExplanationReceiver = blockedIntent?.Operation switch
        {
            SaveRoomExplanation x => x.EntityId,
            SetRoomExplanationArchive x => x.EntityId,
            ReorderRoomExplanation x => x.EntityId,
            DeleteRoomExplanation x => x.ConfirmedArchivedRow.EntityId,
            _ => (Guid?)null
        };
        var missingExplanation = blocked is { Disposition: RoomBlockedDisposition.MissingBlocked,
            Evidence: RoomReloadRequiredEvidence { EntityId: { } missingId } } && missingId == blockedExplanationReceiver
            ? (Guid?)missingId : null;
        var next = new List<RoomTableRow>();
        var header = document.Header.Values;
        var priorSubrooms = rows.OfType<RoomSubroomRow>().Where(x => x.EntityId is not null).ToDictionary(x => x.EntityId!.Value);
        var priorExplanations = rows.OfType<RoomExplanationRow>().Where(x => x.EntityId is not null).ToDictionary(x => x.EntityId!.Value);
        var priorTransitions = rows.OfType<RoomTransitionRow>().Where(x => x.EntityId is not null).ToDictionary(x => x.EntityId!.Value);
        var priorConnections = rows.OfType<RoomConnectionRow>().Where(x => x.EntityId is not null).ToDictionary(x => x.EntityId!.Value);
        var priorChecks = rows.OfType<RoomCheckRow>().Where(x => x.EntityId is not null).ToDictionary(x => x.EntityId!.Value);
        var priorClients = rows.ToLookup(x => x.ClientId);
        foreach (var value in document.Subrooms)
        {
            var prior = priorSubrooms.GetValueOrDefault(value.Observation.EntityId);
            var row = new RoomSubroomRow(prior?.ClientId ?? value.Observation.EntityId, prior?.Initial ?? value.Values)
            { EntityId = value.Observation.EntityId, LastConfirmed = value.Observation, IsArchived = value.Observation.IsArchived, Values = value.Values, Geometry = value.Geometry };
            next.Add(row);
        }
        foreach (var value in document.Explanations)
        {
            var prior = priorExplanations.GetValueOrDefault(value.Observation.EntityId);
            next.Add(new RoomExplanationRow(prior?.ClientId ?? value.Observation.EntityId, prior?.Initial ?? value.Values)
            { EntityId = value.Observation.EntityId, LastConfirmed = value.Observation, IsArchived = value.Observation.IsArchived,
                Values = value.Values, ConfirmedLinkHref = value.Observation.EntityId == missingExplanation
                    ? null : ExplanationRules.ParseLink(value.Values.Link)?.AbsoluteUri });
        }
        foreach (var value in document.Transitions)
        {
            var prior = priorTransitions.GetValueOrDefault(value.Observation.EntityId);
            next.Add(new RoomTransitionRow(prior?.ClientId ?? value.Observation.EntityId, prior?.Initial ?? value.Values)
            { EntityId = value.Observation.EntityId, LastConfirmed = value.Observation, IsArchived = value.Observation.IsArchived,
                Values = value.Values, Metadata = value.Metadata, EnableAnnotation = value.EnableAnnotation });
        }
        foreach (var value in document.Connections)
        {
            var prior = priorConnections.GetValueOrDefault(value.Observation.EntityId);
            next.Add(new RoomConnectionRow(prior?.ClientId ?? value.Observation.EntityId, prior?.Initial ?? value.Values)
            { EntityId = value.Observation.EntityId, LastConfirmed = value.Observation, IsArchived = value.Observation.IsArchived,
                Values = value.Values, X = value.SceneUnitX, Y = value.SceneUnitY, EnableAnnotation = value.EnableAnnotation,
                AttentionSourceSubroomId = value.ResolvedSourceSubroomId, AttentionDestinationSubroomId = value.ResolvedDestinationSubroomId });
        }
        foreach (var value in document.Checks)
        {
            var prior = priorChecks.GetValueOrDefault(value.Observation.EntityId);
            var enemies = CheckLocationTypeCatalogue.IsEnemy(value.Values.LocationType);
            if (blocked is not null && checkOperationSections.TryGetValue(blocked.OperationId, out var sourceSection)
                && pending.Any(x => x.OperationId == blocked.OperationId && OperationRows(x.Operation).Contains(value.Observation.EntityId)))
                enemies = sourceSection == RoomOrderSection.Enemies;
            next.Add(new RoomCheckRow(prior?.ClientId ?? value.Observation.EntityId, prior?.Initial ?? value.Values, enemies)
            { EntityId = value.Observation.EntityId, LastConfirmed = value.Observation, IsArchived = value.Observation.IsArchived,
                Values = value.Values, Metadata = value.Metadata, EnableAnnotation = value.EnableAnnotation });
        }
        // A missing/deleted receiver with retained pending or live work remains an
        // attention owner, not a new uncommitted tail or an implicit recreation.
        var pendingIds = pending.SelectMany(x => OperationRows(x.Operation)).ToHashSet();
        var initialIds = next.Select(x => x.EntityId).ToHashSet();
        var liveClients = native.Where(x => x.Composing || x.Generation > accepted.GetValueOrDefault(Key(x))).Select(x => x.Client).ToHashSet();
        foreach (var old in rows.Where(x => x.EntityId is not null && !initialIds.Contains(x.EntityId)))
        {
            if (restored.ContainsKey(old.ClientId)) continue;
            if (!pendingIds.Contains(old.EntityId!.Value) && !liveClients.Contains(old.ClientId.ToString())) continue;
            var retained = Copy(old); retained.Attention = true; retained.Pending = false; retained.Dependencies = [];
            next.Add(retained);
            initialIds.Add(retained.EntityId);
        }
        foreach (var item in pending)
        {
            var ids = OperationRows(item.Operation).ToHashSet();
            foreach (var row in next.Where(x => x.EntityId is { } id && ids.Contains(id)))
            { row.Pending = true; row.Dependencies = row.Dependencies.Add(item.OperationId); }
            switch (item.Operation)
            {
                case SaveRoomHeader x: header = RoomEditingMerge.Header(x.Origin.Original, x.Submitted, header, new(header, RoomConflictChoice.KeepSubmitted)).Values; break;
                case CreateRoomSubroom x: Create(x.EntityId, RoomOrderSection.Subrooms, row => ((RoomSubroomRow)row).Values = x.Submitted, item); break;
                case CreateRoomExplanation x: Create(x.EntityId, RoomOrderSection.Explanations, row => ((RoomExplanationRow)row).Values = x.Submitted, item); break;
                case CreateRoomTransition x: Create(x.EntityId, RoomOrderSection.Transitions, row => ((RoomTransitionRow)row).Values = x.Submitted, item); break;
                case CreateRoomConnection x: Create(x.EntityId, RoomOrderSection.Connections, row => ((RoomConnectionRow)row).Values = x.Submitted, item); break;
                case ScaffoldRoomConnectionInverse x when Find<RoomConnectionRow>(x.SourceEntityId) is { } source:
                    Create(x.NewEntityId, RoomOrderSection.Connections, row => ((RoomConnectionRow)row).Values = new(source.Values.Alias, source.Values.FriendlyName,
                        source.Values.DestinationSubroomReferenceText, source.Values.SourceSubroomReferenceText, "", "", false, null), item); break;
                case CreateRoomCheck x:
                    var original = priorClients[x.EntityId].OfType<RoomCheckRow>().SingleOrDefault();
                    Create(x.EntityId, original is { Enemies: true } ? RoomOrderSection.Enemies : RoomOrderSection.Checks, row => ((RoomCheckRow)row).Values = x.Submitted, item); break;
                case SaveRoomSubroom x when Find<RoomSubroomRow>(x.EntityId) is { } r:
                    r.Values = RoomEditingMerge.Subroom(x.Origin.Original, x.Submitted, r.Values, new(r.Values, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomExplanation x when Find<RoomExplanationRow>(x.EntityId) is { } r:
                    r.Values = RoomEditingMerge.Explanation(x.Origin.Original, x.Submitted, r.Values, new(r.Values, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomTransition x when Find<RoomTransitionRow>(x.EntityId) is { } r:
                    r.Values = RoomEditingMerge.Transition(x.Origin.Original, x.Submitted, r.Values, new(r.Values, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomConnection x when Find<RoomConnectionRow>(x.EntityId) is { } r:
                    r.Values = RoomEditingMerge.Connection(x.Origin.Original, x.Submitted, r.Values, new(r.Values, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomCheck x when Find<RoomCheckRow>(x.EntityId) is { } r:
                    r.Values = RoomEditingMerge.Check(x.Origin.Original, x.Submitted, r.Values, new(r.Values, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomTransitionMetadata x when Find<RoomTransitionRow>(x.EntityId) is { } r:
                    r.Metadata = RoomEditingMerge.Metadata(x.Origin.Original, x.Submitted, r.Metadata, new(r.Metadata, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomCheckMetadata x when Find<RoomCheckRow>(x.EntityId) is { } r:
                    r.Metadata = RoomEditingMerge.Metadata(x.Origin.Original, x.Submitted, r.Metadata, new(r.Metadata, RoomConflictChoice.KeepSubmitted)).Values; break;
                case SaveRoomSubroomGeometry x when Find<RoomSubroomRow>(x.EntityId) is { } r:
                    var rectangle = RoomEditingMerge.Geometry(x.Origin.Original, x.Submitted, r.Geometry, new(r.Geometry, RoomConflictChoice.KeepSubmitted)).Values;
                    r.Geometry = r.Geometry with { X = rectangle.X, Y = rectangle.Y, Width = rectangle.Width, Height = rectangle.Height }; break;
                case ChangeRoomSubroomAnnotation x when Find<RoomSubroomRow>(x.EntityId) is { } r:
                    r.Geometry = x.Action == RoomSubroomAnnotationAction.Clear ? r.Geometry with { X = null, Y = null, Width = null, Height = null } : r.Geometry with { EnableAnnotation = x.Action == RoomSubroomAnnotationAction.Show }; break;
                case ChangeRoomTransitionAnnotation x when Find<RoomTransitionRow>(x.EntityId) is { } r:
                    var transitionAnnotation = ProjectAnnotation(x.Origin.Original, x.Action, x.Position, r.Metadata, r.EnableAnnotation);
                    r.Metadata = transitionAnnotation.Metadata; r.EnableAnnotation = transitionAnnotation.Enabled; break;
                case ChangeRoomCheckAnnotation x when Find<RoomCheckRow>(x.EntityId) is { } r:
                    var checkAnnotation = ProjectAnnotation(x.Origin.Original, x.Action, x.Position, r.Metadata, r.EnableAnnotation);
                    r.Metadata = checkAnnotation.Metadata; r.EnableAnnotation = checkAnnotation.Enabled; break;
                case ChangeRoomConnectionAnnotation x:
                    var originalCanonical = x.Origin.Members.Single(m => m.EntityId == x.Origin.CanonicalEntityId);
                    var currentCanonical = Find<RoomConnectionRow>(x.Origin.CanonicalEntityId);
                    var desiredX = x.Action is RoomConnectionAnnotationAction.Place or RoomConnectionAnnotationAction.Move ? x.Position!.X
                        : x.Action == RoomConnectionAnnotationAction.Clear ? null : originalCanonical.X;
                    var desiredY = x.Action is RoomConnectionAnnotationAction.Place or RoomConnectionAnnotationAction.Move ? x.Position!.Y
                        : x.Action == RoomConnectionAnnotationAction.Clear ? null : originalCanonical.Y;
                    var desiredEnabled = x.Action == RoomConnectionAnnotationAction.Clear ? originalCanonical.EnableAnnotation : x.Action != RoomConnectionAnnotationAction.Disable;
                    var groupX = originalCanonical.X == desiredX ? currentCanonical is null ? originalCanonical.X : currentCanonical.X : desiredX;
                    var groupY = originalCanonical.Y == desiredY ? currentCanonical is null ? originalCanonical.Y : currentCanonical.Y : desiredY;
                    var groupEnabled = originalCanonical.EnableAnnotation == desiredEnabled ? currentCanonical?.EnableAnnotation ?? originalCanonical.EnableAnnotation : desiredEnabled;
                    foreach (var member in x.Origin.Members)
                    {
                        if (Find<RoomConnectionRow>(member.EntityId) is not { } connection) continue;
                        connection.X = groupX; connection.Y = groupY; connection.EnableAnnotation = groupEnabled;
                    }
                    break;
                case SetRoomSubroomArchive x: Archive(x.EntityId, x.Archived); break;
                case SetRoomExplanationArchive x: Archive(x.EntityId, x.Archived); break;
                case SetRoomTransitionArchive x: Archive(x.EntityId, x.Archived); break;
                case SetRoomConnectionArchive x: Archive(x.EntityId, x.Archived); break;
                case SetRoomCheckArchive x: Archive(x.EntityId, x.Archived); break;
                case DeleteRoomSubroom x: Remove(x.ConfirmedArchivedRow.EntityId); break;
                case DeleteRoomExplanation x: Remove(x.ConfirmedArchivedRow.EntityId); break;
                case DeleteRoomTransition x: Remove(x.ConfirmedArchivedRow.EntityId); break;
                case DeleteRoomConnection x: Remove(x.ConfirmedArchivedRow.EntityId); break;
                case DeleteRoomCheck x: Remove(x.ConfirmedArchivedRow.EntityId); break;
                case ReorderRoomSubroom x: Reorder(x.EntityId, x.TargetIndex); break;
                case ReorderRoomExplanation x: Reorder(x.EntityId, x.TargetIndex); break;
                case ReorderRoomTransition x: Reorder(x.EntityId, x.TargetIndex); break;
                case ReorderRoomConnection x: Reorder(x.EntityId, x.TargetIndex); break;
                case ReorderRoomCheck x: Reorder(x.EntityId, x.TargetIndex); break;
            }
        }
        if (blockedIntent is not null)
        {
            var attentionIds = OperationRows(blockedIntent.Operation).ToHashSet();
            foreach (var row in next.Where(x => x.EntityId is { } id && attentionIds.Contains(id))) { row.Attention = true; row.Pending = false; }
        }
        var nextClients = next.Select(x => x.ClientId).ToHashSet();
        var nativeClients = native.ToLookup(x => x.Client);
        foreach (var section in Enum.GetValues<RoomOrderSection>())
        {
            foreach (var draft in restored.Values.Where(x => Section(x) == section))
                if (nextClients.Add(draft.ClientId)) next.Add(Copy(draft));
            var id = tails[section];
            // The old tail's Initial native frame is immutable, including through
            // creation. The latent successor is allocated once, never per render.
            var old = priorClients[id].SingleOrDefault(x => x.EntityId is null);
            var tail = old is null ? Empty(section, id) : Copy(old);
            var draftTail = Copy(tail);
            var fields = nativeClients[id.ToString()].ToDictionary(x => x.Field);
            ReadRow(draftTail, (field, current) => fields.TryGetValue(field, out var value) ? value.Value : current);
            // A reverted creator resumes its original draft identity. Keep a
            // meaningful already-mounted successor, but do not append a second
            // blank tail or allocate another identity during result installation.
            if (!nextClients.Contains(id) && (fields.Values.Any(x => x.Composing) || Meaningful(draftTail) || !restored.Values.Any(x => Section(x) == section))) { next.Add(tail); nextClients.Add(id); }
        }
        return Materialize(header, next);

        T? Find<T>(Guid id) where T : RoomTableRow => next.OfType<T>().SingleOrDefault(x => x.EntityId == id);
        void Create(Guid id, RoomOrderSection section, Action<RoomTableRow> values, RoomQueuedOperation operation)
        {
            var row = next.SingleOrDefault(x => x.EntityId == id);
            if (row is null)
            {
                var old = priorClients[id].SingleOrDefault();
                row = old is null ? Empty(section, id) : Copy(old);
                row.EntityId = id; row.LastConfirmed = null; row.Dependencies = [];
                next.Add(row);
            }
            values(row); row.Pending = true;
            if (!row.Dependencies.Contains(operation.OperationId)) row.Dependencies = row.Dependencies.Add(operation.OperationId);
        }
        void Archive(Guid id, bool archived) { if (next.SingleOrDefault(x => x.EntityId == id) is { } row) row.IsArchived = archived; }
        void Remove(Guid id) => next.RemoveAll(x => x.EntityId == id);
        void Reorder(Guid id, int index)
        {
            var row = next.SingleOrDefault(x => x.EntityId == id);
            if (row is null) return;
            var family = next.Where(x => Section(x) == Section(row) && x.IsArchived == row.IsArchived && x.EntityId is not null).ToList();
            var previousIndex = family.IndexOf(row);
            family.Remove(row);
            if (previousIndex < index) index--;
            family.Insert(Math.Clamp(index, 0, family.Count), row);
            var positions = next.Select((value, position) => (value, position)).Where(x => Section(x.value) == Section(row) && x.value.IsArchived == row.IsArchived && x.value.EntityId is not null).Select(x => x.position).ToArray();
            for (var i = 0; i < positions.Length; i++) next[positions[i]] = family[i];
        }
    }
    private static (RoomInGameMetadata Metadata, bool Enabled) ProjectAnnotation(RoomDirectAnnotation original, RoomDirectAnnotationAction action,
        RoomAnnotationPoint? position, RoomInGameMetadata current, bool enabled)
    {
        var submitted = action switch
        {
            RoomDirectAnnotationAction.Place or RoomDirectAnnotationAction.Move => original with { X = position!.X, Y = position.Y, EnableAnnotation = true },
            RoomDirectAnnotationAction.Remove => original with { EnableAnnotation = false },
            RoomDirectAnnotationAction.Show or RoomDirectAnnotationAction.ShowAndSelect => original with { EnableAnnotation = true },
            RoomDirectAnnotationAction.Clear => original with { X = null, Y = null },
            RoomDirectAnnotationAction.Reset => original with { X = current.InGamePositionX, Y = current.InGamePositionY, EnableAnnotation = true },
            _ => throw new InvalidOperationException()
        };
        return (current with
        {
            AnnotationSceneUnitX = original.X == submitted.X ? current.AnnotationSceneUnitX : submitted.X,
            AnnotationSceneUnitY = original.Y == submitted.Y ? current.AnnotationSceneUnitY : submitted.Y
        }, original.EnableAnnotation == submitted.EnableAnnotation ? enabled : submitted.EnableAnnotation);
    }
    private static IEnumerable<Guid> OperationRows(RoomEditOperation operation) => operation switch
    {
        CreateRoomSubroom x => [x.EntityId], SaveRoomSubroom x => [x.EntityId], ReorderRoomSubroom x => [x.EntityId], SetRoomSubroomArchive x => [x.EntityId], DeleteRoomSubroom x => [x.ConfirmedArchivedRow.EntityId], SaveRoomSubroomGeometry x => [x.EntityId], ChangeRoomSubroomAnnotation x => [x.EntityId],
        CreateRoomExplanation x => [x.EntityId], SaveRoomExplanation x => [x.EntityId], ReorderRoomExplanation x => [x.EntityId], SetRoomExplanationArchive x => [x.EntityId], DeleteRoomExplanation x => [x.ConfirmedArchivedRow.EntityId],
        CreateRoomTransition x => [x.EntityId], SaveRoomTransition x => [x.EntityId], RequestRoomTransitionInverse x => [x.EntityId], SaveRoomTransitionMetadata x => [x.EntityId], ReorderRoomTransition x => [x.EntityId], SetRoomTransitionArchive x => [x.EntityId], DeleteRoomTransition x => [x.ConfirmedArchivedRow.EntityId], ChangeRoomTransitionAnnotation x => [x.EntityId],
        CreateRoomConnection x => [x.EntityId], SaveRoomConnection x => [x.EntityId], ScaffoldRoomConnectionInverse x => [x.SourceEntityId, x.NewEntityId], ReorderRoomConnection x => [x.EntityId], SetRoomConnectionArchive x => [x.EntityId], DeleteRoomConnection x => [x.ConfirmedArchivedRow.EntityId], ChangeRoomConnectionAnnotation x => x.Origin.Members.Select(r => r.EntityId),
        CreateRoomCheck x => [x.EntityId], SaveRoomCheck x => [x.EntityId], SaveRoomCheckMetadata x => [x.EntityId], ReorderRoomCheck x => [x.EntityId], SetRoomCheckArchive x => [x.EntityId], DeleteRoomCheck x => [x.ConfirmedArchivedRow.EntityId], ChangeRoomCheckAnnotation x => [x.EntityId], _ => []
    };
}
