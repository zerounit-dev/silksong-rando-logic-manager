using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services.Publishing;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

public partial class DocumentationPublishing
{
    private ElementReference reviewHost;
    private DotNetObjectReference<DocumentationPublishing>? trackReference;
    private IJSObjectReference? trackObserver;
    private PublishingReviewLayout? reviewLayout;
    private readonly Queue<(long Revision, PublishingReviewLayout Layout)> anchorLayouts = new();
    private long layoutRevision, indexedRevision = -1;
    private long initialBandHeight;
    private BandWindow? bandWindow;
    private bool readingBand;
    private sealed record BandWindow(long Origin, long First, long Last, MountedSlice[] Slices);
    private sealed record MountedSlice(PublishingReviewLayout.Span Span, long Start, int First, RenderFragment? Body);
    private static string ZoneKey(PublishingReviewZone zone) => "z:" + zone.Name;
    private static string RoomKey(PublishingReviewRoom room) => "r:" + room.Id;
    private static string FileKey(PublishingReviewFile file) => "f:" + file.Entry;

    private void ReviewChanged()
    {
        layoutRevision++;
        bandWindow = null;
    }

    private PublishingReviewLayout ReviewLayout(PublishingRoomReview review, long epoch)
    {
        if (indexedRevision == layoutRevision) return reviewLayout!;
        List<PublishingReviewLayout.Span> spans = [];
        foreach (var zone in review.Zones)
        {
            var key = ZoneKey(zone);
            spans.Add(new(key, null, 38, ZoneToggle(review, epoch, zone), LabelBytes: LabelSize(key, zone.Name)));
            if (openZones.Contains(zone.Name))
                foreach (var room in zone.Rooms) AddRoom(room, key);
        }
        if (review.Publication is { } publication) AddRoom(publication, null);
        if (review.Unmapped is { } unmapped) AddRoom(unmapped, null);
        var layout = new PublishingReviewLayout(spans);
        if (reviewLayout is { } prior)
        {
            layout.CarryMeasurements(prior);
            anchorLayouts.Enqueue((indexedRevision, prior));
            if (anchorLayouts.Count > 4) anchorLayouts.Dequeue();
        }
        (reviewLayout, indexedRevision) = (layout, layoutRevision);
        initialBandHeight = Math.Min(layout.Total, PublishingReviewLayout.BandHeight);
        return layout;

        void AddRoom(PublishingReviewRoom room, string? zone)
        {
            var roomKey = RoomKey(room);
            spans.Add(new(roomKey, zone, 34, RoomToggle(review, epoch, room), LabelBytes: LabelSize(roomKey, room.Name)));
            if (!openRooms.TryGetValue(room.Id, out var state)) return;
            if (state.Room is not { } payload)
            {
                spans.Add(new(roomKey + ":status", roomKey, 40, RoomStatus(room, state), LabelBytes: LabelSize(roomKey, state.Error ?? "")));
                return;
            }
            Func<Task<bool>> admit = () => AdmitWindow(review.Generation, epoch, room.Id, state);
            for (var i = 0; i < payload.Files.Count; i++)
            {
                var file = payload.Files[i];
                var fileIndex = i;
                var key = FileKey(file);
                spans.Add(new(key, roomKey, 36, FileHeading(file, key), Admit: admit, LabelBytes: LabelSize(key, file.Change.Path)));
                if (file.Text is not { } text)
                {
                    var detail = key + ":detail";
                    var image = file.Change.Kind == "Image";
                    spans.Add(new(detail, key, image ? 400 : 46,
                        FileDetail(payload, fileIndex, review.Generation, epoch, state, detail), Admit: admit, LabelBytes: LabelSize(detail, file.Change.Path)));
                    continue;
                }
                if (text.Notice is { } notice)
                    spans.Add(new(key + ":note", key, 44, FileNotice(notice, key + ":note"), Admit: admit, LabelBytes: LabelSize(key, notice)));
                if (text.Projection.Blocks.Count == 0) continue;
                if (!state.Index.TryGetValue(file.Entry, out var index))
                    state.Index[file.Entry] = index = new(text.Projection, Expanded(state, file.Entry));
                var rowKey = key + ":rows";
                var reveal = EventCallback.Factory.Create<PublishingDiffRows.Item>(this, item => RevealRows(state, file.Entry, item, admit));
                spans.Add(new(rowKey, key, 0, null, index, items => DiffRows(file, index, items, rowKey, state.WidthOwner, reveal), admit));
            }
        }
    }

    // Escaped dynamic labels/attributes are charged separately from the bounded
    // fixed section templates. Some labels also occur as keys or alternate text.
    private static int LabelSize(string key, string text) => (int)Math.Min(int.MaxValue,
        4L * (Encoding.UTF8.GetByteCount(HtmlEncoder.Default.Encode(key)) + (long)Encoding.UTF8.GetByteCount(HtmlEncoder.Default.Encode(text))));

    private static HashSet<PublishingDiffProjection.Block> Expanded(RoomDisclosureState state, int entry)
    {
        if (!state.Expanded.TryGetValue(entry, out var folds)) state.Expanded.Add(entry, folds = new(ReferenceEqualityComparer.Instance));
        return folds;
    }

    private async Task RevealRows(RoomDisclosureState state, int entry, PublishingDiffRows.Item item, Func<Task<bool>> admit)
    {
        if (item.Fold is not { } fold || Expanded(state, entry).Contains(fold) || !await admit()) return;
        if (!Expanded(state, entry).Add(fold)) return;
        state.Index.Remove(entry);
        ReviewChanged();
    }

    public sealed record ReviewMeasure(string Key, int Height);
    public sealed record ReviewColumns(string Key, long Left, int Count);
    public sealed record ReviewTrackState(long Revision, long Total, PublishingReviewLayout.Anchor Anchor, bool Mounted,
        PublishingReviewLayout.HeightChange[]? Geometry = null);

    [JSInvokable]
    public Task<ReviewTrackState?> MapReviewAnchor(long revision, long oldRevision, PublishingReviewLayout.Anchor anchor) => InvokeOnReview(() =>
    {
        if (!TrackCurrent(revision) || !double.IsFinite(anchor.Position) || !double.IsFinite(anchor.Offset)) return null;
        var layout = reviewLayout!;
        var mapped = anchorLayouts.FirstOrDefault(x => x.Revision == oldRevision).Layout is { } old
            ? layout.Reanchor(old, anchor) : layout.AnchorAt(anchor.Position);
        return new(revision, layout.Total, mapped, false);
    });

    [JSInvokable]
    public Task<ReviewTrackState?> ReadReviewViewport(long revision, double top, int viewport, ReviewColumns[]? columns,
        ReviewMeasure[]? measurements) => InvokeOnReview(async () =>
    {
        if (!TrackCurrent(revision) || readingBand || !double.IsFinite(top) || reviewLayout is not { Banded: true } layout) return null;
        readingBand = true;
        try
        {
            top = Math.Clamp(top, -PublishingReviewLayout.MaxViewport, layout.Total + PublishingReviewLayout.MaxViewport);
            var anchor = layout.AnchorAt(top);
            PublishingReviewLayout.HeightChange[]? geometry = null;
            if (measurements is { Length: > 0 and <= 512 } && bandWindow is { } mounted)
            {
                var allowed = mounted.Slices.Where(x => x.Span.Rows is null).Select(x => x.Span.Key).ToHashSet(StringComparer.Ordinal);
                var previousTotal = layout.Total;
                geometry = layout.MeasureMounted(measurements.Where(x => allowed.Contains(x.Key)).Select(x => (x.Key, x.Height)));
                anchor = anchor.Key is not null ? layout.Reanchor(layout, anchor)
                    : top >= previousTotal ? anchor with { Position = top + layout.Total - previousTotal } : anchor;
            }
            var requested = layout.Window(anchor.Position, viewport);
            foreach (var admit in requested.Select(x => x.Span.Admit).OfType<Func<Task<bool>>>().Distinct())
            {
                if (!await admit() || !TrackCurrent(revision))
                {
                    StateHasChanged();
                    return null;
                }
            }
            if (!TrackCurrent(revision)) return null;
            var origin = (long)Math.Floor(anchor.Position);
            var horizontal = (columns ?? []).Take(512).DistinctBy(x => x.Key).ToDictionary(x => x.Key, StringComparer.Ordinal);
            bandWindow = new(origin, origin - PublishingReviewLayout.Overscan,
                origin + Math.Clamp(viewport, 1, PublishingReviewLayout.MaxViewport) + PublishingReviewLayout.Overscan,
                requested.Select(x => new MountedSlice(x.Span, x.Start, x.First, Render(x))).ToArray());
            StateHasChanged();
            return new(revision, layout.Total, anchor, true, geometry);

            RenderFragment? Render(PublishingReviewLayout.Slice slice)
            {
                if (slice.Span.Rows is not { } rows) return slice.Span.Body;
                var range = rows.HasOutlier ? horizontal.GetValueOrDefault(slice.Span.Key) : null;
                const int horizontalOverscan = 112; // Larger than the JS 96-column refresh tolerance.
                var left = Math.Clamp(range?.Left ?? 0, 0, rows.Columns);
                var first = Math.Max(0, left - horizontalOverscan);
                var last = left + Math.Clamp(range?.Count ?? 256, 1, 1_024) + horizontalOverscan;
                return slice.Span.RenderRows!(rows.Read(slice.First, slice.Count, first, last));
            }
        }
        finally { readingBand = false; }
    });

    private bool TrackCurrent(long revision) => !disposed && !EffectiveBusy && view?.Review is not null && indexedRevision == revision && layoutRevision == revision;
    private Task<ReviewTrackState?> InvokeOnReview(Func<ReviewTrackState?> action) => InvokeOnReview(() => Task.FromResult(action()));
    private async Task<ReviewTrackState?> InvokeOnReview(Func<Task<ReviewTrackState?>> action)
    {
        ReviewTrackState? result = null;
        await InvokeAsync(async () => { result = await action(); });
        return result;
    }

    private async Task ObserveReviewTrack()
    {
        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./publishing-review-track.js");
            if (disposed) return;
            trackReference = DotNetObjectReference.Create(this);
            var observer = await module.InvokeAsync<IJSObjectReference>("observe", reviewHost, trackReference);
            if (disposed) { await observer.InvokeVoidAsync("dispose"); await observer.DisposeAsync(); }
            else trackObserver = observer;
        }
        catch (JSDisconnectedException) { }
        catch (Exception exception) { Logger.LogError(exception, "Installing the publishing review track failed."); }
    }

    private async Task DisposeReviewTrack()
    {
        try
        {
            if (trackObserver is not null)
            {
                await trackObserver.InvokeVoidAsync("dispose");
                await trackObserver.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        finally { trackReference?.Dispose(); }
    }
}
