using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class LogicCatalogService
{
    internal async Task<RoomMutationEffect?> ChangeTrackedDirectAnnotationAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var (id, origin, action, position, transition) = tracker.CurrentHead?.Operation switch
        {
            Wire.ChangeRoomTransitionAnnotation operation => (operation.EntityId, operation.Origin, operation.Action, operation.Position, true),
            Wire.ChangeRoomCheckAnnotation operation => (operation.EntityId, operation.Origin, operation.Action, operation.Position, false),
            _ => throw new RoomTrackingProtocolException("The head is not a direct annotation action.")
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var exit = transition ? await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId && !x.IsArchived) : null;
        var check = !transition ? await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId && !x.IsArchived) : null;
        if (exit is null && check is null) { RoomEditingMutationScope.Missing(tracker, id); return null; }
        var current = exit is not null
            ? new Wire.RoomDirectAnnotation(exit.AnnotationSceneUnitX, exit.AnnotationSceneUnitY, exit.EnableAnnotation, exit.InGamePositionX, exit.InGamePositionY)
            : new Wire.RoomDirectAnnotation(check!.AnnotationSceneUnitX, check.AnnotationSceneUnitY, check.EnableAnnotation, check.InGamePositionX, check.InGamePositionY);
        var valid = action switch
        {
            Wire.RoomDirectAnnotationAction.Place => position is not null && AnnotationPair(position.X, position.Y),
            Wire.RoomDirectAnnotationAction.Move => current.EnableAnnotation && AnnotationPair(current.X, current.Y)
                && position is not null && AnnotationPair(position.X, position.Y),
            Wire.RoomDirectAnnotationAction.Show or Wire.RoomDirectAnnotationAction.ShowAndSelect => current.EnableAnnotation || AnnotationPair(current.X, current.Y),
            Wire.RoomDirectAnnotationAction.Reset => AnnotationPair(current.InGamePositionX, current.InGamePositionY),
            _ => true
        };
        if (!valid)
        {
            RoomEditingMutationScope.Corrective(tracker, id, "This annotation action is no longer available. Copy pending work before reloading.");
            return null;
        }
        var original = origin.Original;
        if (action == Wire.RoomDirectAnnotationAction.Reset && tracker.DirectAnnotationReview is { } review
            && (current.InGamePositionX != review.Reviewed.InGamePositionX || current.InGamePositionY != review.Reviewed.InGamePositionY))
        {
            // Both choices require the game basis actually displayed in review.
            tracker.DirectAnnotationReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                "The game position changed elsewhere. Review the current reset values.", new Wire.RoomDirectAnnotationConflictEvidence(current));
            return null;
        }
        var submitted = action switch
        {
            Wire.RoomDirectAnnotationAction.Place or Wire.RoomDirectAnnotationAction.Move => original with { X = position!.X, Y = position.Y, EnableAnnotation = true },
            Wire.RoomDirectAnnotationAction.Remove => original with { EnableAnnotation = false },
            Wire.RoomDirectAnnotationAction.Show or Wire.RoomDirectAnnotationAction.ShowAndSelect => original with { EnableAnnotation = true },
            Wire.RoomDirectAnnotationAction.Clear => original with { X = null, Y = null },
            Wire.RoomDirectAnnotationAction.Reset => original with { X = current.InGamePositionX, Y = current.InGamePositionY, EnableAnnotation = true },
            _ => throw new RoomTrackingProtocolException("Unknown direct annotation action.")
        };
        var merge = new AnnotationMerge(tracker.DirectAnnotationReview?.Choice);
        var reviewed = tracker.DirectAnnotationReview?.Reviewed ?? current;
        var x = merge.Value(original.X, submitted.X, current.X, reviewed.X);
        var y = merge.Value(original.Y, submitted.Y, current.Y, reviewed.Y);
        var enabled = merge.Value(original.EnableAnnotation, submitted.EnableAnnotation, current.EnableAnnotation, reviewed.EnableAnnotation);
        if (!merge.CanApply)
        {
            tracker.DirectAnnotationReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                "This annotation changed elsewhere. Review the current values.", new Wire.RoomDirectAnnotationConflictEvidence(current));
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        if (exit is not null) { exit.AnnotationSceneUnitX = x; exit.AnnotationSceneUnitY = y; exit.EnableAnnotation = enabled; }
        else { check!.AnnotationSceneUnitX = x; check.AnnotationSceneUnitY = y; check.EnableAnnotation = enabled; }
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, tx, transition ? RoomProjectionFamilies.Transitions : RoomProjectionFamilies.Checks,
            Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport);
    }

    internal async Task<RoomMutationEffect?> ChangeTrackedSubroomAnnotationAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation as Wire.ChangeRoomSubroomAnnotation
            ?? throw new RoomTrackingProtocolException("The head is not a subroom annotation action.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var row = await db.Subrooms.SingleOrDefaultAsync(x => x.Id == operation.EntityId && x.RoomId == tracker.Identity.RoomId && !x.IsArchived);
        if (row is null) { RoomEditingMutationScope.Missing(tracker, operation.EntityId); return null; }
        var current = new Wire.RoomSubroomGeometry(row.SceneUnitX, row.SceneUnitY, row.SceneUnitWidth, row.SceneUnitHeight, row.EnableAnnotation);
        if (operation.Action is Wire.RoomSubroomAnnotationAction.Show or Wire.RoomSubroomAnnotationAction.Hide
            && !ValidRectangle(current.X, current.Y, current.Width, current.Height))
        {
            RoomEditingMutationScope.Corrective(tracker, row.Id, "This subroom annotation has no complete rectangle. Copy pending work before reloading.");
            return null;
        }
        var original = operation.Origin.Original;
        var reviewed = tracker.GeometryReview?.Reviewed ?? current;
        var merge = new AnnotationMerge(tracker.GeometryReview?.Choice);
        var clear = operation.Action == Wire.RoomSubroomAnnotationAction.Clear;
        var x = merge.Value(original.X, clear ? null : original.X, current.X, reviewed.X, Wire.RoomGeometryField.X);
        var y = merge.Value(original.Y, clear ? null : original.Y, current.Y, reviewed.Y, Wire.RoomGeometryField.Y);
        var width = merge.Value(original.Width, clear ? null : original.Width, current.Width, reviewed.Width, Wire.RoomGeometryField.Width);
        var height = merge.Value(original.Height, clear ? null : original.Height, current.Height, reviewed.Height, Wire.RoomGeometryField.Height);
        var enabled = merge.Value(original.EnableAnnotation, clear ? original.EnableAnnotation : operation.Action == Wire.RoomSubroomAnnotationAction.Show,
            current.EnableAnnotation, reviewed.EnableAnnotation, Wire.RoomGeometryField.EnableAnnotation);
        if (!merge.CanApply)
        {
            tracker.GeometryReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                "This subroom annotation changed elsewhere. Review the current values.",
                new Wire.RoomGeometryConflictEvidence(current, new(clear ? null : original.X, clear ? null : original.Y,
                    clear ? null : original.Width, clear ? null : original.Height), merge.GeometryFields.ToImmutableArray()));
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        row.SceneUnitX = x; row.SceneUnitY = y; row.SceneUnitWidth = width; row.SceneUnitHeight = height; row.EnableAnnotation = enabled;
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Subrooms, Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport);
    }

    internal async Task<RoomMutationEffect?> ChangeTrackedConnectionAnnotationAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation as Wire.ChangeRoomConnectionAnnotation
            ?? throw new RoomTrackingProtocolException("The head is not a connection annotation action.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var rows = await db.SubroomConnections.Where(x => x.RoomId == tracker.Identity.RoomId && !x.IsArchived).ToListAsync();
        var source = rows.SingleOrDefault(x => x.Id == operation.EntityId);
        if (source is null) { RoomEditingMutationScope.Missing(tracker, operation.EntityId); return null; }
        var subrooms = await db.Subrooms.Where(x => x.RoomId == tracker.Identity.RoomId && !x.IsArchived).ToListAsync();
        var group = ConnectionAnnotations.FindEligibleGroup(source, rows, subrooms);
        if (group is null)
        {
            RoomEditingMutationScope.Corrective(tracker, source.Id, "This connection is no longer eligible for an annotation. Copy pending work before reloading.");
            return null;
        }
        var canonical = group.Rows[0];
        var current = new Wire.RoomConnectionAnnotationOrigin(group.Rows.Select(x => new Wire.RoomConnectionAnnotationMember(
            x.Id, x.Alias, x.SortOrder, x.IsArchived, x.SceneUnitX, x.SceneUnitY, x.EnableAnnotation, null)).ToImmutableArray(), canonical.Id, []);
        var original = operation.Origin;
        // Membership/canonical identity is a domain guard, never a force-overwrite
        // choice. Do not silently retarget an alias action onto a different group.
        if (original.CanonicalEntityId != current.CanonicalEntityId || !original.Members.Select(x => x.EntityId).ToHashSet().SetEquals(current.Members.Select(x => x.EntityId))
            || original.Members.Any(x => current.Members.Single(y => y.EntityId == x.EntityId) is var now
                && (!string.Equals(x.Alias.Trim(), now.Alias.Trim(), StringComparison.OrdinalIgnoreCase) || x.IsArchived != now.IsArchived)))
        {
            RoomEditingMutationScope.Corrective(tracker, source.Id, "This annotation group changed. Copy pending work before reloading.");
            return null;
        }
        var action = operation.Action;
        if ((action is Wire.RoomConnectionAnnotationAction.Move && (!canonical.EnableAnnotation || !AnnotationPair(canonical.SceneUnitX, canonical.SceneUnitY)))
            || (action is Wire.RoomConnectionAnnotationAction.Show or Wire.RoomConnectionAnnotationAction.ShowAndSelect && !AnnotationPair(canonical.SceneUnitX, canonical.SceneUnitY))
            || (action is Wire.RoomConnectionAnnotationAction.Place or Wire.RoomConnectionAnnotationAction.Move
                && (operation.Position is null || !AnnotationPair(operation.Position.X, operation.Position.Y))))
        {
            RoomEditingMutationScope.Corrective(tracker, source.Id, "This annotation action is no longer available. Copy pending work before reloading.");
            return null;
        }
        var capturedCanonical = original.Members.Single(x => x.EntityId == original.CanonicalEntityId);
        var desiredX = action is Wire.RoomConnectionAnnotationAction.Place or Wire.RoomConnectionAnnotationAction.Move ? operation.Position!.X
            : action == Wire.RoomConnectionAnnotationAction.Clear ? (double?)null : capturedCanonical.X;
        var desiredY = action is Wire.RoomConnectionAnnotationAction.Place or Wire.RoomConnectionAnnotationAction.Move ? operation.Position!.Y
            : action == Wire.RoomConnectionAnnotationAction.Clear ? (double?)null : capturedCanonical.Y;
        var desiredEnabled = action == Wire.RoomConnectionAnnotationAction.Clear ? capturedCanonical.EnableAnnotation : action != Wire.RoomConnectionAnnotationAction.Disable;
        var merge = new AnnotationMerge(tracker.ConnectionAnnotationReview?.Choice);
        // One logical canonical result, followed by the domain's atomic sibling
        // synchronization. Raw sibling origins/reviews remain evidence, not separate
        // independently merged row-save intentions that could split the group.
        var reviewed = tracker.ConnectionAnnotationReview?.Reviewed.Members.Single(x => x.EntityId == canonical.Id)
            ?? current.Members.Single(x => x.EntityId == canonical.Id);
        var x = merge.Value(capturedCanonical.X, desiredX, canonical.SceneUnitX, reviewed.X);
        var y = merge.Value(capturedCanonical.Y, desiredY, canonical.SceneUnitY, reviewed.Y);
        var enabled = merge.Value(capturedCanonical.EnableAnnotation, desiredEnabled, canonical.EnableAnnotation, reviewed.EnableAnnotation);
        if (!merge.CanApply)
        {
            tracker.ConnectionAnnotationReview = null;
            RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                "This annotation group changed elsewhere. Review the current raw values.", new Wire.RoomConnectionAnnotationConflictEvidence(current));
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        foreach (var row in group.Rows) { row.SceneUnitX = x; row.SceneUnitY = y; row.EnableAnnotation = enabled; }
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Connections, Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport);
    }

    private static bool AnnotationPair(double? x, double? y) => x.HasValue && y.HasValue && double.IsFinite(x.Value) && double.IsFinite(y.Value);

    // Same four cases as the authored typed merge, with explicit field calls above.
    // Reviews authorize original differences only and must still match current data.
    private sealed class AnnotationMerge(Wire.RoomConflictChoice? choice)
    {
        public bool CanApply { get; private set; } = true;
        public List<Wire.RoomGeometryField> GeometryFields { get; } = [];
        public T Value<T>(T original, T submitted, T current, T reviewed, Wire.RoomGeometryField? geometryField = null)
        {
            var equality = EqualityComparer<T>.Default;
            if (equality.Equals(original, submitted)) return current;
            if (choice.HasValue && !equality.Equals(current, reviewed))
            { CanApply = false; if (geometryField is { } field) GeometryFields.Add(field); }
            if (equality.Equals(current, submitted)) return current;
            if (equality.Equals(current, original)) return submitted;
            if (!choice.HasValue) { CanApply = false; if (geometryField is { } field) GeometryFields.Add(field); return current; }
            return choice == Wire.RoomConflictChoice.KeepServer ? current : submitted;
        }
    }
}
