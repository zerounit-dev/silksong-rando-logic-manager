using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using static Silksong_Rando_Logic_Manager.Services.Publishing.PublishingTextDiff;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

// Immutable layout index over prepared spans. Rows and strips never participate
// in comparison. Visible rows retain independent old/new source identities.
internal sealed class PublishingDiffProjection
{
    internal sealed record Row(int? Old, int? New, string? Html, string? EndingHtml, long Columns, LongLine? Outlier,
        long EscapedBytes, int InlineElements);
    internal sealed record Block(Row[] Rows, int HiddenLines, long Columns, bool HasOutlier, long EscapedBytes, long InlineElements);
    internal readonly record struct Fragment(string Source, int Old, int New, int Length, Kind? Style)
    {
        internal int Start => Old >= 0 ? Old : New;
    }
    private const int InlineLimit = 2_048, Context = 3, BlockLimit = 512;
    private const int InlineMarkLimit = 32, InlineByteLimit = 8 * 1024;
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);
    internal IReadOnlyList<Block> Blocks { get; }

    internal PublishingDiffProjection(PublishingTextDiff diff)
    {
        // Equal decoded content is reconstructible as a shared span, but the
        // byte-only/empty notice already explains why there is no diff to view.
        Blocks = diff.Available && diff.Before == diff.After ? [] : new Builder(diff).Build();
    }

    private sealed class Builder(PublishingTextDiff diff)
    {
        private readonly List<Block> blocks = [];
        private readonly List<Row> ready = [], shared = [];
        private readonly List<Fragment> fragments = [];
        private readonly List<(Kind? Style, string Label)> endings = [];
        private int? oldStart, newStart;
        private long length;
        private bool changed, sawChange, baseNoEnding, candidateNoEnding;
        private bool pendingBreak;
        private Kind? breakStyle;

        internal IReadOnlyList<Block> Build()
        {
            if (diff.Available)
            {
                foreach (var span in diff.Spans)
                {
                    var source = span.Old >= 0 ? diff.Before! : diff.After!;
                    Add(source, span.Old >= 0 ? span.Old : span.New,
                        span.Length, span.Old, span.New, span.Kind);
                }
            }
            else
            {
                // A readable side is neutral evidence, not an invented delta.
                if (diff.BeforePresent && diff.Before is { Length: > 0 } before) Add(before, 0, before.Length, 0, -1, null);
                if (diff.AfterPresent && diff.After is { Length: > 0 } after) Add(after, 0, after.Length, -1, 0, null);
            }
            if (oldStart is not null || newStart is not null) FinishRow();
            FinishShared(tail: false);
            FinishBlock();
            return blocks.AsReadOnly();
        }

        private void Add(string source, int start, int count, int old, int @new, Kind? style)
        {
            for (int used = 0; used < count;)
            {
                var remaining = source.AsSpan(start + used, count - used);
                var atEnding = remaining[0] is '\r' or '\n';
                // Adjacent alternative ending cues can share one display break.
                // There is no row matching: only immediately adjacent literal
                // terminators from opposite changed spans may do this.
                bool alternative = atEnding && pendingBreak && breakStyle == Kind.Removed && style == Kind.Inserted;
                if (pendingBreak && !alternative) FinishRow();
                oldStart ??= old >= 0 ? old + used : null;
                newStart ??= @new >= 0 ? @new + used : null;
                changed |= style is Kind.Removed or Kind.Inserted;
                if (atEnding)
                {
                    var size = EndingLength(remaining, 0);
                    if (remaining[0] == '\r' || style is Kind.Removed or Kind.Inserted)
                        endings.Add((style, size == 2 ? "CRLF" : remaining[0] == '\r' ? "CR" : "LF"));
                    used += size;
                    pendingBreak = true;
                    breakStyle = style;
                }
                else
                {
                    int size = remaining.IndexOfAny('\r', '\n');
                    if (size < 0) size = remaining.Length;
                    fragments.Add(new(source, old < 0 ? -1 : old + used, @new < 0 ? -1 : @new + used, size, style));
                    used += size;
                    length += size;
                    baseNoEnding |= old >= 0 && old + used == diff.Before!.Length;
                    candidateNoEnding |= @new >= 0 && @new + used == diff.After!.Length;
                }
            }
        }

        private void FinishRow()
        {
            if (baseNoEnding || candidateNoEnding)
            {
                // If both compared sides have no terminator here, share the cue.
                // Otherwise say which side ended, even on a mixed display row.
                if (!diff.Available || baseNoEnding && candidateNoEnding) endings.Add((null, "no newline"));
                else if (baseNoEnding) endings.Insert(0, (Kind.Removed, "no newline (base)"));
                else endings.Add((Kind.Inserted, "no newline (candidate)"));
            }
            // A physically short row can still be expensive to mount. Bound
            // each full row (even in the vertical band), and give exceptionally
            // dense rows the same column window as exceptionally long ones.
            var text = length <= InlineLimit && fragments.Count <= InlineMarkLimit ? new StringBuilder() : null;
            var cue = endings.Count != 0 ? new StringBuilder() : null;
            long cueColumns = 0;
            int inline = cue is null ? 0 : 1; // Terminal cue wrapper.
            if (text is not null)
                foreach (var fragment in fragments)
                    inline += Append(text, fragment.Source.AsSpan(fragment.Start, fragment.Length), fragment.Style);
            foreach (var (style, label) in endings)
            {
                var tag = style == Kind.Removed ? "del" : style == Kind.Inserted ? "ins" : "span";
                cue!.Append('<').Append(tag).Append(" class=\"pr-eol\">").Append(label).Append("</").Append(tag).Append('>');
                cueColumns += Math.Max(12, label.Length + 3);
                inline++;
            }

            var html = text?.ToString(); var endingHtml = cue?.ToString();
            var textBytes = html is null ? 0 : Encoding.UTF8.GetByteCount(html);
            var outlier = html is null || textBytes > InlineByteLimit ? new LongLine(fragments.ToArray()) : null;
            var row = new Row(oldStart, newStart, outlier is null ? html : null, endingHtml, (outlier?.Columns ?? 0) + cueColumns, outlier,
                (outlier is null ? textBytes : 0) + Encoding.UTF8.GetByteCount(endingHtml ?? ""), inline);
            if (changed || !diff.Available)
            {
                FinishShared(tail: true);
                Queue(row);
                sawChange = true;
            }
            else shared.Add(row);

            oldStart = newStart = null;
            length = 0;
            changed = baseNoEnding = candidateNoEnding = pendingBreak = false;
            breakStyle = null;
            fragments.Clear(); endings.Clear();
        }

        private void FinishShared(bool tail)
        {
            var head = sawChange ? Math.Min(Context, shared.Count) : 0;
            var keepTail = tail ? Math.Min(Context, shared.Count - head) : 0;
            var hidden = shared.Count - head - keepTail;
            if (hidden <= 2)
            {
                foreach (var row in shared) Queue(row);
            }
            else
            {
                for (int i = 0; i < head; i++) Queue(shared[i]);
                FinishBlock();
                var folded = new Row[hidden];
                shared.CopyTo(head, folded, 0, hidden);
                AddBlock(folded, hidden);
                for (int i = shared.Count - keepTail; i < shared.Count; i++) Queue(shared[i]);
            }
            shared.Clear();
        }

        private void Queue(Row row)
        {
            ready.Add(row);
            if (ready.Count == BlockLimit) FinishBlock();
        }

        private void FinishBlock()
        {
            if (ready.Count == 0) return;
            AddBlock(ready.ToArray(), 0);
            ready.Clear();
        }

        private void AddBlock(Row[] rows, int hidden)
        {
            long columns = 0, bytes = 0, inline = 0;
            foreach (var row in rows)
            {
                if (row.Outlier is not null) columns = Math.Max(columns, row.Columns);
                bytes += row.EscapedBytes;
                inline += row.InlineElements;
            }
            blocks.Add(new(rows, hidden, columns, columns != 0, bytes, inline));
        }
    }

    // Exceptional rows use an index into styled source fragments. The viewport
    // escapes only intersecting tiles. A cut cannot split a Unicode scalar.
    internal sealed class LongLine
    {
        private const int Tile = 64;
        private readonly Fragment[] fragments;
        private readonly Cut[] cuts;
        private readonly record struct Cut(int Fragment, int Offset, long Column);
        internal long Columns => cuts[^1].Column;

        internal LongLine(Fragment[] fragments)
        {
            this.fragments = fragments;
            List<Cut> index = [new(0, 0, 0)];
            int remaining = Tile;
            long column = 0;
            for (int i = 0; i < fragments.Length; i++)
            {
                var f = fragments[i];
                for (int j = 0; j < f.Length;)
                {
                    var rune = Rune.GetRuneAt(f.Source, f.Start + j);
                    if (remaining < rune.Utf16SequenceLength)
                    {
                        index.Add(new(i, j, column));
                        remaining = Tile;
                    }
                    j += rune.Utf16SequenceLength;
                    remaining -= rune.Utf16SequenceLength;
                    column += rune.Value == '\t' ? 4 - column % 4
                        : PaintCodePoint(rune, f.Style) ? (rune.IsBmp ? 8 : rune.Value <= 0xFFFFF ? 9 : 10) : rune.IsAscii ? 1 : 2;
                }
            }
            index.Add(new(fragments.Length, 0, column));
            cuts = index.ToArray();
        }

        internal sealed record Piece(long Start, long End, string Html);
        internal sealed record Strip(Piece[] Pieces, bool ShowEnding);

        internal Strip? Read(long first, long end, long rowEnd)
        {
            if (first >= end || end <= 0 || first >= rowEnd) return null;
            var showEnding = rowEnd > Columns && end > Columns;
            if (first >= Columns) return new([], showEnding);
            var a = Math.Max(0, LowerBound(first) - 1);
            if (cuts[a + 1].Column == first) a++;
            var z = Math.Min(cuts.Length - 1, Math.Max(a + 1, LowerBound(end)));
            var pieces = new Piece[z - a];
            for (int i = a; i < z; i++)
            {
                var from = cuts[i]; var to = cuts[i + 1];
                var html = new StringBuilder();
                for (int f = from.Fragment; f < to.Fragment || f == to.Fragment && to.Offset > 0; f++)
                {
                    var fragment = fragments[f];
                    int start = f == from.Fragment ? from.Offset : 0;
                    int stop = f == to.Fragment ? to.Offset : fragment.Length;
                    if (stop > start) Append(html, fragment.Source.AsSpan(fragment.Start + start, stop - start), fragment.Style);
                }
                pieces[i - a] = new(from.Column, to.Column, html.ToString());
            }
            return new(pieces, showEnding);
        }

        private int LowerBound(long column)
        {
            int lo = 0, hi = cuts.Length;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (cuts[mid].Column < column) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    private static int Append(StringBuilder html, ReadOnlySpan<char> text, Kind? style)
    {
        var tag = style == Kind.Removed ? "del" : style == Kind.Inserted ? "ins" : null;
        if (tag is not null)
        {
            html.Append('<').Append(tag);
            if (text.IsWhiteSpace()) html.Append(" class=\"pr-whitespace\"");
            html.Append('>');
        }
        // HTML's numeric references can reinterpret C0/C1 controls. Preserve
        // tabs and label non-displayable controls explicitly in the paint only.
        int start = 0;
        int i = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (PaintCodePoint(rune, style))
            {
                Encode(html, text[start..i]);
                html.Append("[U+").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append(']');
                start = i + rune.Utf16SequenceLength;
            }
            i += rune.Utf16SequenceLength;
        }
        Encode(html, text[start..]);
        if (tag is not null) html.Append("</").Append(tag).Append('>');
        return tag is null ? 0 : 1;

        static void Encode(StringBuilder builder, ReadOnlySpan<char> s) =>
            builder.Append(Encoder.Encode(s.ToString()).Replace("&#x9;", "\t", StringComparison.Ordinal));
    }

    private static bool PaintCodePoint(Rune rune, Kind? style) => rune.Value != '\t' && (Rune.IsControl(rune)
        || (style is Kind.Removed or Kind.Inserted) && (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
            || rune.Value is 0x034F or >= 0x180B and <= 0x180D or 0x180F or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF));
}
