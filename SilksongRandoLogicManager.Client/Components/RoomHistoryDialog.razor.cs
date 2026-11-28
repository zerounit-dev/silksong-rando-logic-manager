using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomHistoryDialog
{
    [Parameter, EditorRequired] public EditorHistoryFamily Family { get; set; }
    [Parameter, EditorRequired] public string Kind { get; set; } = "";
    [Parameter, EditorRequired] public string Name { get; set; } = "";
    [Parameter] public IReadOnlyList<EditorHistoryRevision>? Revisions { get; set; }
    [Parameter, EditorRequired] public string FocusKey { get; set; } = null!;
    [Parameter, EditorRequired] public IReadOnlyDictionary<long, LocalTime> Times { get; set; } = null!;
    [Parameter] public bool Unavailable { get; set; }
    [Parameter, EditorRequired] public EventCallback Close { get; set; }
    [Parameter, EditorRequired] public Action<string> FocusTarget { get; set; } = null!;

    public sealed record LocalTime(string DayKey, bool IsToday, string Date, string Time, string Full);
    private sealed record StatusValue(string Label, string? Glyph = null, string? Text = null);
    private sealed record Column(string Label, Func<EditorHistoryValues, object?> Read,
        Func<object?, string?> Format, int? Width, string? Class = null, Func<EditorHistoryValues, string?>? Display = null,
        bool HiddenWithoutDetails = false, Func<object?, StatusValue?>? Status = null, bool InlineDiff = false,
        string? HeadingGlyph = null, Func<object?, string?>? ExactCoordinate = null)
    {
        public string? TrackStyle { get; } = Width is null ? null : FormattableString.Invariant($"--history-track:{Width}ch");
        public string CompactTrackClass => Width is null ? "history-coordinate" : "history-compact";
        public string? Render(EditorHistoryValues values) => Display is null ? Format(Read(values)) : Display(values);
    }
    private sealed record Group(List<EditorHistoryRevision> Members);
    private readonly List<Group> groups = [];
    private readonly Dictionary<long, EditorHistoryRevision> older = [];
    private readonly HashSet<long> expanded = [];
    // The same resulting revision can also have a different group-net baseline.
    private readonly record struct TextCell(long RowId, long BaselineId, Column Field);
    private readonly Dictionary<TextCell, RoomHistoryTextDiff.Result> textDiffs = [];
    private int textBudget = RoomHistoryTextDiff.OpeningWork;
    private bool showDiffs = true;
    private IReadOnlyList<EditorHistoryRevision>? assembled;
    private bool showDetails;
    private IEnumerable<Column> Columns => showDetails ? Fields[Family] : Fields[Family].Where(column => !column.HiddenWithoutDetails);

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(assembled, Revisions)) return;
        assembled = Revisions;
        older.Clear();
        textDiffs.Clear();
        if (assembled is not null)
            for (var i = 0; i + 1 < assembled.Count; ++i) older.Add(assembled[i].RowId, assembled[i + 1]);
        Regroup();
    }

    private void Regroup()
    {
        expanded.Clear(); groups.Clear();
        if (assembled is null) return;
        Group? group = null;
        for (var i = assembled.Count - 1; i >= 0; --i)
        {
            var row = assembled[i];
            if (Family != EditorHistoryFamily.Room && !showDetails && row.DetailsOnly) continue;
            if (Family == EditorHistoryFamily.Room && older.TryGetValue(row.RowId, out var previous)
                && Fields[Family].All(field => Equals(field.Read(row.Values), field.Read(previous.Values)))) continue;
            if (group is null || row.ActorId != group.Members[0].ActorId || row.ChangeUtc - group.Members[0].ChangeUtc > TimeSpan.FromHours(2))
            {
                group = new([]);
                groups.Add(group);
            }
            group.Members.Add(row);
        }
    }

    // Only a multi-revision summary may compare to its own first member when there
    // is no stored predecessor. Individual revisions have no such fallback.
    private EditorHistoryRevision? ResolveBaseline(EditorHistoryRevision oldest, bool summary = false) =>
        older.GetValueOrDefault(oldest.RowId) ?? (summary ? oldest : null);

    private void ToggleDetails(ChangeEventArgs args) { showDetails = args.Value is true; Regroup(); }
    private void Toggle(long id) { if (!expanded.Remove(id)) expanded.Add(id); }
    private bool AllExpanded => groups.Where(g => g.Members.Count > 1).All(g => expanded.Contains(g.Members[^1].RowId));
    private void ToggleAll()
    {
        if (AllExpanded) expanded.Clear();
        else foreach (var group in groups.Where(g => g.Members.Count > 1)) expanded.Add(group.Members[^1].RowId);
    }
    private Task KeyDownAsync(KeyboardEventArgs e) => e.Key == "Escape" ? Close.InvokeAsync() : Task.CompletedTask;
    private static string DisclosureId(Group group) => $"v3-room-history-disclosure-{group.Members[^1].RowId}";
    private void FocusStart() => FocusTarget("v3-room-history-close");
    private void FocusEnd()
    {
        var lastDisclosure = groups.FirstOrDefault(group => group.Members.Count > 1);
        FocusTarget(lastDisclosure is not null ? DisclosureId(lastDisclosure) : groups.Count > 0
            ? "v3-room-history-viewport" : "v3-room-history-close");
    }

    private RoomHistoryTextDiff.Result? DescribeText(TextCell cell, string? before, string? current)
    {
        if (textDiffs.TryGetValue(cell, out var cached)) return cached;
        var result = RoomHistoryTextDiff.Compare(before, current, ref textBudget);
        if (result is not null) textDiffs.Add(cell, result);
        return result;
    }

    private static RenderFragment InlineText(string before, string text, RoomHistoryTextDiff.Result? diff) => builder =>
    {
        if (diff is null)
        {
            if (before.Length > 0) { builder.OpenElement(0, "del"); builder.AddContent(1, before); builder.CloseElement(); }
            if (text.Length > 0)
            {
                builder.OpenElement(2, "ins");
                if (before.Length > 0) builder.AddAttribute(3, "class", "history-replacement");
                builder.AddContent(4, text);
                builder.CloseElement();
            }
            return;
        }
        var at = 0;
        foreach (var edit in diff.Edits)
        {
            if (edit.After.Start > at) builder.AddContent(5, text[at..edit.After.Start]);
            if (edit.Before.Length > 0)
            {
                builder.OpenElement(6, "del");
                builder.AddContent(7, Slice(before, edit.Before));
                builder.CloseElement();
            }
            if (edit.After.Length > 0)
            {
                builder.OpenElement(8, "ins");
                if (edit.Before.Length > 0) builder.AddAttribute(9, "class", "history-replacement");
                builder.AddContent(10, Slice(text, edit.After));
                builder.CloseElement();
            }
            at = edit.After.End;
        }
        if (at < text.Length) builder.AddContent(11, at == 0 ? text : text[at..]);
    };

    private static string Slice(string text, RoomHistoryTextDiff.Range range) => text[range.Start..range.End];

    private static string GridTime(LocalTime stamp) => stamp.IsToday ? stamp.Time : $"{stamp.Date} {stamp.Time}";
    private static string Coordinate(double? n, bool exact)
    {
        var text = n?.ToString(exact ? "R" : "0.##", CultureInfo.InvariantCulture) ?? "—";
        return !exact && text == "-0" ? "0" : text;
    }
    private static string? Point(object? value, bool triple, bool exact) => value is EditorHistoryPoint p && (p.X is not null || p.Y is not null || p.Z is not null)
        ? triple ? $"({Coordinate(p.X, exact)}, {Coordinate(p.Y, exact)}, {Coordinate(p.Z, exact)})"
            : $"({Coordinate(p.X, exact)}, {Coordinate(p.Y, exact)})" : null;
    private static string? Rectangle(object? value, bool exact) => value is EditorHistoryRectangle r && (r.X is not null || r.Y is not null || r.Width is not null || r.Height is not null)
        ? $"({Coordinate(r.X, exact)}, {Coordinate(r.Y, exact)}) · {Coordinate(r.Width, exact)}×{Coordinate(r.Height, exact)}" : null;
    private static Column CoordinateColumn(string label, Func<EditorHistoryValues, object?> read, Func<object?, bool, string?> format) =>
        new(label, read, value => format(value, false), null, HiddenWithoutDetails: true,
            ExactCoordinate: value => format(value, true));
    private static string CoordinateTitle(string? before, string? after) => $"Before: {before ?? "—"}\nAfter: {after ?? "—"}";
    private readonly record struct GameLine(string? Display, bool Changed, string? Title)
    {
        public bool Visible => Display is not null || Changed;
    }
    private static GameLine DescribeGameLine(EditorHistoryPoint? value, EditorHistoryPoint? before, bool hasBaseline)
    {
        var changed = hasBaseline && !Equals(value, before);
        var display = Point(value, true, false);
        var title = changed && display is not null && string.Equals(display, Point(before, true, false), StringComparison.Ordinal)
            ? CoordinateTitle(Point(before, true, true), Point(value, true, true)) : null;
        return new(display, changed, title);
    }
    private static Column Text(string label, Func<EditorHistoryValues, string?> read, int width, bool inlineDiff = false, bool hiddenWithoutDetails = false) =>
        new(label, read, v => (string?)v, width, InlineDiff: inlineDiff, HiddenWithoutDetails: hiddenWithoutDetails);
    private static Column Flag(string label, Func<EditorHistoryValues, bool> read, string yes, string no, string? headingGlyph,
        int width = 3, bool hiddenWithoutDetails = false) => new(label, v => read(v), _ => null, width, "history-icon",
        HiddenWithoutDetails: hiddenWithoutDetails,
        Status: v => v is true ? new(yes, Text: "Y") : new(no, Text: "N"), HeadingGlyph: headingGlyph);
    private static string TypeName(object? value) => value is not string { Length: > 0 } type ? "unknown"
        : CheckLocationTypeCatalogue.Definitions.FirstOrDefault(item => item.OutputValue == type)?.Name ?? $"Unrecognized: {type}";

    private static readonly Column NameColumn = Text("Name", v => v.Name, 12);
    private static readonly Column Alias = Text("Alias", v => v.Alias, 5);
    private static readonly Column Requirements = Text("Requirements", v => v.Requirements, 30, inlineDiff: true);
    private static readonly Column Notes = Text("Notes", v => v.Notes, 28, inlineDiff: true);
    private static readonly Column Todo = Flag("TODO", v => v.Todo, "TODO", "Not TODO", headingGlyph: null, width: 4);
    private static readonly Column Verify = new("Verify", v => v.Verify, _ => null, 3, "history-icon", Status: v => v switch
    {
        true => new("Verified", "fa-circle-check"),
        false => new("Not verified", "fa-circle-xmark"),
        _ => new("Verification unknown", "fa-circle-minus")
    }, HeadingGlyph: "fa-clipboard-check");
    private static readonly Column GameId = Text("In-game ID", v => v.InGameId, 7, hiddenWithoutDetails: true);
    // This composite cell renders its two typed values separately, not via the scalar field path.
    private static readonly Column GameCoordinates = new("Game coordinates", _ => null, _ => null, null, HiddenWithoutDetails: true);
    private static readonly Column Enabled = Flag("Annotation enabled", v => v.AnnotationEnabled, "Annotation enabled", "Annotation disabled",
        "fa-location-dot", hiddenWithoutDetails: true);
    private static readonly Column State = new("State", v => v.State, _ => null, 5, "history-icon", Status: v => v switch
    {
        EditorHistoryState.Active => new("Active", Text: "—"),
        EditorHistoryState.Archived => new("Archived", "fa-box-archive"),
        EditorHistoryState.Deleted => new("Deleted", "fa-trash"),
        _ => null
    });
    private static readonly Column PointColumn = CoordinateColumn("Annotation position", v => v.AnnotationPoint, (value, exact) => Point(value, false, exact));
    private static readonly Column RectangleColumn = CoordinateColumn("Annotation rectangle", v => v.AnnotationRectangle, Rectangle);

    private static readonly Dictionary<EditorHistoryFamily, Column[]> Fields = new()
    {
        [EditorHistoryFamily.Subroom] = [NameColumn, Text("Reference", v => v.Reference, 12), Notes, Enabled, State, RectangleColumn],
        [EditorHistoryFamily.Explanation] = [NameColumn, Text("Reference", v => v.Reference, 12), Text("Link", v => v.Link, 24), Notes, State],
        [EditorHistoryFamily.Transition] = [Alias, NameColumn, Text("From subroom", v => v.Source, 12), Text("Destination room", v => v.Destination, 12),
            Text("Destination alias", v => v.DestinationAlias, 11), Flag("Unsafe arrival", v => v.UnsafeArrival, "Unsafe arrival", "Not marked unsafe", "fa-triangle-exclamation"), Requirements, Notes, Todo, Verify,
            GameId, GameCoordinates, Enabled, State, PointColumn],
        [EditorHistoryFamily.Connection] = [Alias, NameColumn, Text("Source", v => v.Source, 12), Text("Destination", v => v.Destination, 12),
            Requirements, Notes, Todo, Verify, Enabled, State, PointColumn],
        [EditorHistoryFamily.Check] = [new("Type", v => v.Type, TypeName, 12), NameColumn, Text("Subroom", v => v.Source, 12), Requirements, Notes,
            Todo, Verify, GameId, GameCoordinates, Enabled, State, PointColumn],
        [EditorHistoryFamily.Room] = [NameColumn, Text("Reference", v => v.Reference, 12), Text("Game ID", v => v.InGameId, 7),
            Text("Contributors", v => v.RoomDetails!.Contributors, 18), Text("Comments", v => v.RoomDetails!.Comments, 36, inlineDiff: true),
            new("Room group", v => v.RoomDetails!.GroupId, _ => null, 12, Display: v => v.RoomDetails!.GroupId is null
                ? "Ungrouped" : string.IsNullOrWhiteSpace(v.RoomDetails.GroupName) ? "Unavailable room group" : v.RoomDetails.GroupName), State]
    };
}
