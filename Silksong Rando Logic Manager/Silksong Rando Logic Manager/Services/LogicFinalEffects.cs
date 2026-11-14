using System.Collections.Immutable;
using System.Globalization;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Transaction-original versus frozen FINAL rows, after canonical completion.
/// Scope-local evidence only: never retained table history or possible dependency owners.</summary>
internal sealed class LogicFinalEffects
{
    internal sealed record RoomFact(Guid Id, DateTime CacheFlagUtc, Guid? GroupId, string FriendlyName, int SortOrder)
    {
        internal RoomDocumentObservation Observation => new(Id, CacheFlagUtc);
    }
    internal sealed record RoomChange(RoomFact? Original, RoomFact? Final);
    internal sealed record GroupChange(SharedPublicationCoordinator.GroupWriteFact? Original,
        SharedPublicationCoordinator.GroupWriteFact? Final);
    internal sealed record MapLinkFact(Guid Id, Guid ZoneId, string InGameId, string? ReferenceText, Guid? ResolvedRoomId);
    internal sealed record MapLinkChange(MapLinkFact? Original, MapLinkFact? Final);

    internal ImmutableArray<LogicChangeCapture.Change> Rows { get; }
    internal ImmutableDictionary<Guid, RoomChange> Rooms { get; }
    internal ImmutableDictionary<Guid, GroupChange> Groups { get; }
    internal ImmutableDictionary<Guid, MapLinkChange> MapLinks { get; }
    internal ImmutableHashSet<Guid?> MembershipOrderCollections { get; }
    internal bool GroupOrderChanged { get; }
    internal bool RoomFactsChanged => !Rooms.IsEmpty || !Groups.IsEmpty;
    internal bool MapLinksChanged => MapLinks.Values.Any(x => x.Original != x.Final);

    internal LogicFinalEffects(IReadOnlyList<LogicChangeCapture.Change> final)
    {
        Rows = final.ToImmutableArray();
        var rooms = ImmutableDictionary.CreateBuilder<Guid, RoomChange>();
        var groups = ImmutableDictionary.CreateBuilder<Guid, GroupChange>();
        var links = ImmutableDictionary.CreateBuilder<Guid, MapLinkChange>();
        var collections = ImmutableHashSet.CreateBuilder<Guid?>();
        var groupOrderChanged = false;
        foreach (var change in Rows)
        {
            if (change.Table is not (LogicCacheTable.Rooms or LogicCacheTable.RoomGroups or LogicCacheTable.MapScenes)) continue;
            var columns = change.Columns.Select((name, ordinal) => (name, ordinal))
                .ToDictionary(x => x.name, x => x.ordinal, StringComparer.Ordinal);
            Row? Decode(LogicCacheRow? row) => row is null ? null : new Row(columns, row);
            var original = Decode(change.Before);
            var current = Decode(change.After);
            var id = Guid.Parse(change.Key);
            if (id == Guid.Empty || original is { } old && old.Id != id || current is { } next && next.Id != id)
                throw new InvalidOperationException("Final capture identity does not match its typed row.");
            switch (change.Table)
            {
                case LogicCacheTable.Rooms:
                    var before = original is { } r0 ? Room(r0) : null;
                    var after = current is { } r1 ? Room(r1) : null;
                    rooms.Add(id, new(before, after));
                    if (before is null || after is null || before.GroupId != after.GroupId
                        || before.SortOrder != after.SortOrder || before.FriendlyName != after.FriendlyName)
                    {
                        if (before is not null) collections.Add(before.GroupId);
                        if (after is not null) collections.Add(after.GroupId);
                    }
                    break;
                case LogicCacheTable.RoomGroups:
                    var g0 = original is { } oldGroup ? Group(oldGroup) : null;
                    var g1 = current is { } newGroup ? Group(newGroup) : null;
                    groups.Add(id, new(g0, g1));
                    groupOrderChanged |= g0 is null || g1 is null || g0.SortOrder != g1.SortOrder || g0.FriendlyName != g1.FriendlyName;
                    break;
                case LogicCacheTable.MapScenes:
                    links.Add(id, new(original is { } s0 ? Link(s0) : null, current is { } s1 ? Link(s1) : null));
                    break;
            }
        }
        Rooms = rooms.ToImmutable(); Groups = groups.ToImmutable(); MapLinks = links.ToImmutable();
        MembershipOrderCollections = collections.ToImmutable();
        GroupOrderChanged = groupOrderChanged;
    }

    private static RoomFact Room(Row row) => new(row.Id, new DateTime(row.Integer("CacheFlagUtc"), DateTimeKind.Utc),
        row.NullableGuid("RoomGroupId"), row.Text("FriendlyName"), row.Int("SortOrder"));
    private static SharedPublicationCoordinator.GroupWriteFact Group(Row row) => new(row.Id,
        row.Text("FriendlyName"), row.Int("SortOrder"), row.NullableText("ZoneReferenceText"),
        row.NullableGuid("ResolvedMapZoneId"), row.Bool("IsVirtual"), row.Utc("CreatedUtc"), row.Utc("UpdatedUtc"));
    private static MapLinkFact Link(Row row) => new(row.Id, row.Guid("MapZoneId"), row.Text("InGameId"),
        row.NullableText("RoomReferenceText"), row.NullableGuid("ResolvedRoomId"));

    private readonly struct Row
    {
        private readonly IReadOnlyDictionary<string, int> columns;
        private readonly LogicCacheRow row;
        internal Row(IReadOnlyDictionary<string, int> columns, LogicCacheRow row)
        {
            if (columns.Count != row.Values.Count) throw new InvalidOperationException("Incomplete finalized capture row.");
            this.columns = columns; this.row = row;
        }
        private LogicCacheCell Cell(string name) => columns.TryGetValue(name, out var ordinal) ? row.Values[ordinal]
            : throw new InvalidOperationException($"Finalized capture is missing {name}.");
        internal Guid Id => Guid("Id");
        internal string Text(string name) => Cell(name) is { Type: LogicCacheCellType.Text, Value: { } text } ? text
            : throw new InvalidOperationException($"Finalized {name} is not SQLite TEXT.");
        internal string? NullableText(string name) => Cell(name).Type == LogicCacheCellType.Null ? null : Text(name);
        internal Guid Guid(string name) => System.Guid.Parse(Text(name));
        internal Guid? NullableGuid(string name) => NullableText(name) is { } text ? System.Guid.Parse(text) : null;
        internal long Integer(string name) => Cell(name) is { Type: LogicCacheCellType.Integer, Value: { } text }
            ? long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Finalized {name} is not SQLite INTEGER.");
        internal int Int(string name) => checked((int)Integer(name));
        internal bool Bool(string name) => Integer(name) switch
        { 0 => false, 1 => true, _ => throw new InvalidOperationException($"Finalized {name} is not a SQLite boolean.") };
        internal DateTime Utc(string name) => DateTime.SpecifyKind(DateTime.ParseExact(Text(name),
            ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture,
            DateTimeStyles.None), DateTimeKind.Utc);
    }
}
