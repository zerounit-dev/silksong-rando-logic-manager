using System.Collections.Immutable;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    // Private immutable preparation, not another confirmed owner. Only Install
    // publishes it. Native workspace staging may validate a result before install.
    private sealed record Candidate(ImmutableDictionary<Guid, ConfirmedRoomFacts> Rooms,
        ImmutableArray<CatalogueGroup> Groups, ImmutableArray<Guid> Order,
        ImmutableDictionary<Guid, RoomStatus> Statuses,
        ImmutableDictionary<Guid, ImageState> Images, ConfirmedVocabulary? Vocabulary, ConfirmedManager? Manager,
        Sidebar Sidebar, SidebarPresentation? Presentation, SidebarSearchIndex? Search,
        bool Initialized)
    {
        internal ReadObservations Reads { get; init; } = ReadObservations.Empty();
        internal LookupIdentities Identities { get; init; } = LookupIdentities.Empty;
        internal ImmutableHashSet<Guid> ChangedRooms { get; init; } = [];
        internal RoomEditing.RoomDiagnosticIndex.Facts Diagnostics { get; init; } = RoomEditing.RoomDiagnosticIndex.Empty;
    }

    private Candidate CaptureCandidate() => new(Rooms, Groups, PresentationRoomIds,
        Statuses, Images, Vocabulary, Manager, Sidebar, Presentation, Search, Initialized)
        { Reads = readObservations, Identities = lookupIdentities, Diagnostics = Diagnostics };

    private static Candidate PrepareCandidate(Candidate prior, ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableArray<CatalogueGroup> groups, ImmutableArray<Guid> order,
        ConfirmedVocabulary? vocabulary, IReadOnlyCollection<Guid> changed)
    {
        using var timing = OperationTiming.Start("shared.candidate", enabled: prior.Initialized);
        var identities = PrepareLookupIdentities(prior.Identities, prior.Rooms, rooms, changed);
        var diagnostics = RoomEditing.RoomDiagnosticIndex.Prepare(prior.Diagnostics, rooms, changed);
        if (groups != prior.Groups)
        {
            Unique(groups, x => x.Id);
            if (groups.Any(x => x.FriendlyName is null || x.UpdatedUtc.Kind != DateTimeKind.Utc)) throw new JsonException("Unusable global groups.");
        }
        if (order != prior.Order)
        {
            Unique(order, x => x);
            if (order.Length != rooms.Count || order.Any(id => !rooms.ContainsKey(id))) throw new JsonException("Incomplete global Room order.");
        }
        foreach (var id in changed)
            if (rooms.TryGetValue(id, out var room) && (room.Header.Metadata is null
                || room.Header.RoomGroupId is { } group && !groups.Any(x => x.Id == group)))
                throw new JsonException("Incomplete changed global Room dependency.");
        // Completion is supplied by the authoritative persisted source, including
        // affected dependents. No aggregate validation/index reconstruction per save.
        var statuses = prior.Statuses;
        foreach (var id in changed)
        {
            if (!rooms.TryGetValue(id, out var room)) { statuses = statuses.Remove(id); continue; }
            if (!Enum.IsDefined(room.Header.CompletionStatus)) throw new JsonException("Invalid stored completion.");
            if (!statuses.TryGetValue(id, out var old) || old != room.Header.CompletionStatus)
                statuses = statuses.SetItem(id, room.Header.CompletionStatus);
        }
        var images = PrepareImages(prior, rooms, changed);
        var sameHeaders = changed.All(id => prior.Rooms.GetValueOrDefault(id)?.Header == rooms.GetValueOrDefault(id)?.Header);
        var (sidebar, presentation) = sameHeaders && groups == prior.Groups && order == prior.Order
            && ReferenceEquals(statuses, prior.Statuses) && ReferenceEquals(images, prior.Images)
            ? (prior.Sidebar, prior.Presentation) : PrepareSidebar(prior, rooms, groups, order, statuses, images);
        var search = SidebarSearchIndex.Prepare(rooms, prior.Rooms, prior.Search, changed);
        var manager = ReferenceEquals(vocabulary, prior.Vocabulary) ? prior.Manager : PrepareManager(vocabulary!);
        timing.End();
        return new(rooms, groups, order, statuses, images, vocabulary, manager, sidebar,
            presentation, search, true)
            { Reads = prior.Reads, Identities = identities, Diagnostics = diagnostics, ChangedRooms = prior.ChangedRooms.Union(changed) };
    }

    private void Install(Candidate value, Action? incorporate = null)
    {
        runtime.RequireActive();
        var previous = CaptureCandidate();
        using var timing = OperationTiming.Start("shared.publication", enabled: previous.Initialized);
        var factsChanged = !ReferenceEquals(previous.Rooms, value.Rooms) || previous.Groups != value.Groups
            || !ReferenceEquals(previous.Statuses, value.Statuses) || !ReferenceEquals(previous.Images, value.Images)
            || previous.Order != value.Order || !ReferenceEquals(previous.Vocabulary, value.Vocabulary)
                && (previous.Vocabulary is null || value.Vocabulary is null || !SameVocabularyFacts(previous.Vocabulary, value.Vocabulary));
        Assign(value);
        // No observer runs until both shared storage and prepared workspace/I are
        // installed. A failed ownership guard must not acknowledge shared tokens.
        try { incorporate?.Invoke(); }
        catch { Assign(previous); throw; }
        // Only admitted final facts can renew raw image evidence. An older retained
        // receipt rejected by provenance must not regress a later observation.
        var observations = new List<LogicCacheImageObservation>();
        foreach (var id in value.ChangedRooms)
        {
            if (!value.Rooms.TryGetValue(id, out var room)) continue;
            if ((!previous.Rooms.TryGetValue(id, out var old) || old.Header.Metadata != room.Header.Metadata)
                && room.Header.Metadata.ImageExists is { } exists)
                observations.Add(new(id, exists));
        }
        if (observations.Count != 0) browser.ObserveImages(observations);
        // A bookkeeping-only change is not another fact publication. The owning
        // feature callback still runs once, even when storage was already current.
        if (factsChanged || previous.Initialized != value.Initialized) Changed?.Invoke();
        timing.End();
    }

    private void Assign(Candidate value)
    {
        runtime.RequireActive();
        Rooms = value.Rooms; Groups = value.Groups; PresentationRoomIds = value.Order;
        Statuses = value.Statuses; Images = value.Images;
        Vocabulary = value.Vocabulary; Manager = value.Manager; Sidebar = value.Sidebar;
        Presentation = value.Presentation; Search = value.Search;
        Initialized = value.Initialized;
        readObservations = value.Reads;
        lookupIdentities = value.Identities;
        Diagnostics = value.Diagnostics;
    }
}
