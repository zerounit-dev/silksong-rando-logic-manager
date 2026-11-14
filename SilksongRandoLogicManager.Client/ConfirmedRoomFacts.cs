using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Global lookup/search facts, not editable/concurrency records or sparse protocol DTOs.
// No comments, Notes, geometry, saved parse/verification flags or full child families.
public sealed record RoomLookupIdentity(Guid EntityId, bool IsArchived);
public sealed record RoomLookupText(string FriendlyName, string ReferenceId, string? InGameId);
public sealed record RoomImageIndicatorFacts(bool UseSceneImage, bool UseUploadedSceneImage, bool Stale,
    double? PixelsPerUnitX, double? PixelsPerUnitY);
public sealed record RoomLookupHeader(RoomLookupIdentity Observation, RoomLookupText Values,
    Guid? RoomGroupId, int SortOrder, RoomStatus CompletionStatus, RoomImageIndicatorFacts SceneImage,
    RoomMetadata Metadata);
public sealed record TransitionLookupText(string Alias, string? DestinationRoomReferenceText, string? DestinationTransitionAliasText);
public sealed record TransitionLookupFacts(RoomLookupIdentity Observation, TransitionLookupText Values,
    Guid? ResolvedDestinationRoomId, Guid? ResolvedDestinationTransitionId);
public sealed record CheckLookupText(string FriendlyName);
public sealed record CheckLookupFacts(RoomLookupIdentity Observation, CheckLookupText Values);
public sealed record ConfirmedRoomFacts(RoomDocumentObservation Observation, RoomLookupHeader Header,
    ImmutableArray<TransitionLookupFacts> Transitions, ImmutableArray<CheckLookupFacts> Checks,
    ImmutableArray<string> Requirements)
{
    internal static ConfirmedRoomFacts Project(RoomScalarSnapshot source, ConfirmedRoomFacts? previous = null)
    {
        var h = source.Header; var i = h.SceneImage;
        var header = new RoomLookupHeader(new(h.Observation.EntityId, h.Observation.IsArchived),
            new(h.Values.FriendlyName, h.Values.ReferenceId, h.Values.InGameId), h.RoomGroupId, h.SortOrder,
            h.CompletionStatus, new(i.UseSceneImage, i.UseUploadedSceneImage, i.Stale, i.PixelsPerUnitX, i.PixelsPerUnitY), h.Metadata);
        return Reuse(new(source.Observation, header,
            source.Transitions.Select(x => new TransitionLookupFacts(new(x.Observation.EntityId, x.Observation.IsArchived),
                new(x.Values.Alias, x.Values.DestinationRoomReferenceText, x.Values.DestinationTransitionAliasText),
                x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId)).ToImmutableArray(),
            source.Checks.Select(x => new CheckLookupFacts(new(x.Observation.EntityId, x.Observation.IsArchived), new(x.Values.FriendlyName))).ToImmutableArray(),
            source.Transitions.Where(x => !x.Observation.IsArchived).Select(x => x.Values.Requirements)
                .Concat(source.Connections.Where(x => !x.Observation.IsArchived).Select(x => x.Values.Requirements))
                .Concat(source.Checks.Where(x => !x.Observation.IsArchived).Select(x => x.Values.Requirements)).ToImmutableArray()), previous);
    }

    internal static ConfirmedRoomFacts Reuse(ConfirmedRoomFacts value, ConfirmedRoomFacts? previous)
    {
        if (previous is null) return value;
        var next = value with {
            Header = value.Header == previous.Header ? previous.Header : value.Header,
            Transitions = value.Transitions.SequenceEqual(previous.Transitions) ? previous.Transitions : value.Transitions,
            Checks = value.Checks.SequenceEqual(previous.Checks) ? previous.Checks : value.Checks,
            Requirements = value.Requirements.SequenceEqual(previous.Requirements, StringComparer.Ordinal) ? previous.Requirements : value.Requirements };
        return next == previous ? previous : next;
    }
}
