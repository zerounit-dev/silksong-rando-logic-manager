using Microsoft.AspNetCore.Components;
using Silksong_Rando_Logic_Manager.Services.Publishing;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

public sealed class PublishingDiffRows
{
    private readonly PublishingDiffProjection projection;
    private readonly int[] offsets;
    internal PublishingDiffRows(PublishingDiffProjection projection, HashSet<PublishingDiffProjection.Block> expanded)
    {
        this.projection = projection;
        offsets = new int[projection.Blocks.Count + 1];
        for (var i = 0; i < projection.Blocks.Count; i++)
        {
            var block = projection.Blocks[i];
            var folded = block.HiddenLines != 0 && !expanded.Contains(block);
            offsets[i + 1] = checked(offsets[i] + (folded ? 1 : block.Rows.Length));
            if (folded) continue;
            EscapedBytes += block.EscapedBytes;
            InlineElements += block.InlineElements;
            Columns = Math.Max(Columns, block.Columns);
            HasOutlier |= block.HasOutlier;
        }
    }
    internal const int Height = 22;
    internal int Count => offsets[^1];
    internal long Columns { get; }
    internal bool HasOutlier { get; }
    internal long EscapedBytes { get; }
    internal long InlineElements { get; }

    private int BlockAt(int row)
    {
        var index = Array.BinarySearch(offsets, row);
        return index < 0 ? ~index - 1 : index;
    }

    public readonly struct Item
    {
        internal Item(int index, PublishingDiffProjection.Row? row, PublishingDiffProjection.Block? fold, PublishingDiffProjection.LongLine.Strip? strip = null) => (Index, Row, Fold, Strip) = (index, row, fold, strip);
        internal int Index { get; }
        internal PublishingDiffProjection.Row? Row { get; }
        internal PublishingDiffProjection.Block? Fold { get; }
        internal PublishingDiffProjection.LongLine.Strip? Strip { get; }
    }
    internal Item[] Read(int first, int count, long left = 0, long right = 0)
    {
        var items = new Item[count];
        if (count == 0) return items;
        var b = BlockAt(first);
        for (var n = 0; n < count; n++)
        {
            var i = first + n;
            if (i >= offsets[b + 1]) b++;
            var block = projection.Blocks[b];
            var row = block.Rows[i - offsets[b]];
            items[n] = block.HiddenLines != 0 && offsets[b + 1] - offsets[b] == 1
                ? new(i, null, block) : new(i, row, null, row.Outlier?.Read(left, right, row.Columns));
        }
        return items;
    }

    // Same immutable projected row and both source-side offsets, not a
    // percentage or a source-line count. Revealing a fold changes offsets in
    // the visible track only, not the row identity inside its block.
    internal int Reanchor(PublishingDiffRows prior, int index)
    {
        if (ReferenceEquals(prior, this)) return Math.Clamp(index, 0, Count - 1);
        if (!ReferenceEquals(prior.projection, projection)) return 0;
        var b = prior.BlockAt(Math.Clamp(index, 0, prior.Count - 1));
        var oldSize = prior.offsets[b + 1] - prior.offsets[b];
        var newSize = offsets[b + 1] - offsets[b];
        return offsets[b] + Math.Clamp(index - prior.offsets[b], 0, Math.Min(oldSize, newSize) - 1);
    }
}

// One ordered index across the open review group. Fixed-size runs describe
// text, not individual DOM nodes. Variable-height leaf measurements update a
// prefix tree; scrolling doesn't rescan the disclosures or prepared text.
public sealed class PublishingReviewLayout
{
    internal const int BandHeight = 262_144;
    internal const int MaxSectionHeight = 40_000;
    internal const int Overscan = 880;
    internal const int MaxViewport = 8192;
    private const int PanTail = 22;
    private const int NativeRows = 512, NativeSections = 128, NativeBytes = 192 * 1024, NativeInlineElements = 2_048;
    internal sealed record Span(string Key, string? Parent, int Estimate, RenderFragment? Body,
        PublishingDiffRows? Rows = null, Func<PublishingDiffRows.Item[]?, RenderFragment>? RenderRows = null,
        Func<Task<bool>>? Admit = null, int LabelBytes = 0);

    private readonly Span[] spans;
    private readonly Dictionary<string, int> byKey = new(StringComparer.Ordinal);
    private readonly int[] structuralHeights;
    private readonly long[] tree;
    internal bool Banded { get; }
    internal long Columns { get; }
    internal bool HasOutlier { get; }
    internal long Total => Start(spans.Length);
    // A file heading and its immediate children are contiguous in the ordered
    // layout. Ordinary review uses these same leaves as the band, not a second
    // list of expected suffixes that can drift from the producer.
    internal IEnumerable<Span> FileSpans(string key)
    {
        var i = byKey[key];
        yield return spans[i++];
        for (; i < spans.Length && spans[i].Parent == key; i++) yield return spans[i];
    }

    internal PublishingReviewLayout(List<Span> source)
    {
        spans = source.ToArray();
        structuralHeights = new int[spans.Length];
        tree = new long[spans.Length + 1];
        long nativeHeight = 0, nativeRows = 0, nativeBytes = 0, nativeInlineElements = 0;
        int nativeSections = 0;
        for (var i = 0; i < spans.Length; i++)
        {
            var span = spans[i];
            byKey.Add(span.Key, i);
            if (span.Rows is { } rows)
            {
                var height = (long)rows.Count * PublishingDiffRows.Height;
                Add(i, height + PanTail);
                nativeHeight += height + PanTail;
                nativeRows += rows.Count;
                nativeBytes += rows.EscapedBytes;
                nativeInlineElements += rows.InlineElements;
                Columns = Math.Max(Columns, rows.Columns);
                HasOutlier |= rows.HasOutlier;
            }
            else
            {
                structuralHeights[i] = span.Estimate;
                Add(i, span.Estimate);
                nativeHeight += MaxSectionHeight;
                nativeSections++;
                nativeBytes += span.LabelBytes;
            }
        }
        Banded = HasOutlier || nativeHeight > 8_000_000 || nativeRows > NativeRows ||
            nativeSections > NativeSections || nativeBytes > NativeBytes || nativeInlineElements > NativeInlineElements;
    }

    private void Add(int index, long delta)
    {
        for (var k = index + 1; k < tree.Length; k += k & -k) tree[k] += delta;
    }
    private long Start(int index)
    {
        long result = 0;
        for (var k = index; k > 0; k -= k & -k) result += tree[k];
        return result;
    }
    private int At(double offset)
    {
        int i = 0;
        long sum = 0;
        for (var bit = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)tree.Length); bit != 0; bit >>= 1)
        {
            var next = i + bit;
            if (next < tree.Length && sum + tree[next] <= offset) { i = next; sum += tree[next]; }
        }
        return i;
    }

    internal void Measure(string key, int height)
    {
        if (!byKey.TryGetValue(key, out var i) || spans[i].Rows is not null) return;
        height = Math.Clamp(height, PublishingDiffRows.Height, MaxSectionHeight);
        Add(i, height - structuralHeights[i]);
        structuralHeights[i] = height;
    }

    public sealed record HeightChange(long Start, int Before, int After);

    internal HeightChange[] MeasureMounted(IEnumerable<(string Key, int Height)> measurements)
    {
        // Snapshot every start in the same, pre-measurement coordinate system.
        var changes = measurements.DistinctBy(x => x.Key).Select(x =>
        {
            var index = byKey[x.Key];
            return (x.Key, Change: new HeightChange(Start(index), structuralHeights[index],
                Math.Clamp(x.Height, PublishingDiffRows.Height, MaxSectionHeight)));
        }).Where(x => x.Change.Before != x.Change.After).OrderBy(x => x.Change.Start).ToArray();
        foreach (var change in changes) Measure(change.Key, change.Change.After);
        return changes.Select(x => x.Change).ToArray();
    }

    internal void CarryMeasurements(PublishingReviewLayout prior)
    {
        for (var i = 0; i < spans.Length; i++)
            if (structuralHeights[i] != 0 && prior.byKey.TryGetValue(spans[i].Key, out var old)
                && prior.structuralHeights[old] != prior.spans[old].Estimate)
                Measure(spans[i].Key, prior.structuralHeights[old]);
    }

    // Wire type carries coordinates only. No file content is serialized as a
    // track index, including when an absolute seek skips across the review.
    public sealed record Anchor(string? Key, int Row, double Offset, double Position);
    internal Anchor AnchorAt(double position)
    {
        var i = At(position);
        if (i >= spans.Length || position < 0) return new(null, 0, 0, position);
        var within = position - Start(i);
        var row = spans[i].Rows is { } rows ? Math.Min(rows.Count - 1, (int)(within / PublishingDiffRows.Height)) : 0;
        var offset = spans[i].Rows is null ? within : Math.Min(PublishingDiffRows.Height - 1, within - (long)row * PublishingDiffRows.Height);
        return new(spans[i].Key, row, offset, position);
    }

    internal Anchor Reanchor(PublishingReviewLayout prior, Anchor anchor)
    {
        if (anchor.Key is null)
            return anchor with { Position = anchor.Position >= prior.Total ? Total + anchor.Position - prior.Total : anchor.Position };
        var key = anchor.Key;
        while (!byKey.ContainsKey(key))
        {
            if (!prior.byKey.TryGetValue(key, out var old) || prior.spans[old].Parent is not { } parent)
                return new(null, 0, 0, Math.Min(anchor.Position, Total));
            key = parent;
        }
        var i = byKey[key];
        var row = 0;
        var offset = key == anchor.Key ? Math.Clamp(anchor.Offset, 0, (spans[i].Rows is null ? structuralHeights[i] : PublishingDiffRows.Height) - 1) : 0;
        if (spans[i].Rows is { } rows && prior.byKey.TryGetValue(key, out var previous) && prior.spans[previous].Rows is { } oldRows)
            row = rows.Reanchor(oldRows, anchor.Row);
        return new(key, row, offset, Start(i) + (long)row * PublishingDiffRows.Height + offset);
    }

    internal sealed record Slice(Span Span, long Start, int First, int Count);
    internal Slice[] Window(double top, int viewport)
    {
        var from = Math.Max(0, Math.Floor(top - Overscan));
        var to = Math.Min(Total, Math.Ceiling(top + Math.Clamp(viewport, 1, MaxViewport) + Overscan));
        var result = new List<Slice>();
        var i = At(from);
        var start = Start(i);
        for (; i < spans.Length && start < to; i++)
        {
            var span = spans[i];
            if (span.Rows is { } rows)
            {
                var first = Math.Min(rows.Count - 1, (int)(Math.Max(0, from - start) / PublishingDiffRows.Height));
                var end = Math.Min(rows.Count, (int)Math.Ceiling((to - start) / PublishingDiffRows.Height));
                result.Add(new(span, start + (long)first * PublishingDiffRows.Height, first, end - first));
                start += (long)rows.Count * PublishingDiffRows.Height + PanTail;
            }
            else
            {
                result.Add(new(span, start, 0, 0));
                start += structuralHeights[i];
            }
        }
        return result.ToArray();
    }
}
