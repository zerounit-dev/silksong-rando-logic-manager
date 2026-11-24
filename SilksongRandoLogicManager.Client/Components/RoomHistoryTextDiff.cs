using System.Globalization;
using System.Text;

namespace SilksongRandoLogicManager.Client.Components;

// Presentation only. All ranges index the original UTF-16 string, never a reconstructed value.
internal static class RoomHistoryTextDiff
{
    private const int MaxCharacters = 8_192; // Combined before and after.
    private const int MaxMatrixSlots = 32_768;
    private const int MaxCellWork = 180_000;
    private const int MaxEdits = 12; // At most 37 shared/removed/inserted fragments.
    private const int CoarseEdgeLimit = 256;
    private const int CoarseCueLimit = 256;
    internal const int OpeningWork = 2_000_000;

    internal readonly record struct Range(int Start, int End)
    {
        public int Length => End - Start;
    }

    internal readonly record struct Edit(Range Before, Range After);
    internal enum ChangeCue { None, Whitespace, Unclassified }
    internal sealed record Result(Edit[] Edits, ChangeCue Cue);
    private readonly record struct Anchor(int Before, int After, int Count);

    // Null means no budget for edge or cue work: render the whole strings and a generic cue.
    internal static Result? Compare(string? before, string? after, ref int budget)
    {
        var oldText = before ?? "";
        var text = after ?? "";
        var chars = (long)oldText.Length + text.Length;
        // Includes scanning, trimming, constructing runs and the small memo entry.
        if (chars > MaxCharacters || !Spend(256 + 8 * (int)chars, ref budget)) return Coarse(oldText, text, ref budget);

        var oldTokens = Tokenize(oldText);
        var tokens = Tokenize(text);
        bool Equal(int i, int j) => oldText.AsSpan(oldTokens[i].Start, oldTokens[i].Length)
            .SequenceEqual(text.AsSpan(tokens[j].Start, tokens[j].Length));

        var shared = Math.Min(oldTokens.Count, tokens.Count);
        var prefix = 0;
        while (prefix < shared && Equal(prefix, prefix)) ++prefix;
        var oldEnd = oldTokens.Count;
        var end = tokens.Count;
        while (oldEnd > prefix && end > prefix && Equal(oldEnd - 1, end - 1)) { --oldEnd; --end; }

        var oldCount = oldEnd - prefix;
        var count = end - prefix;
        var stride = count + 1;
        ushort[]? lengths = null;
        if (oldCount > 0 && count > 0)
        {
            var slots = (oldCount + 1) * stride;
            if (slots > MaxMatrixSlots) return Coarse(oldText, text, ref budget);
            var oldWidth = oldTokens[oldEnd - 1].End - oldTokens[prefix].Start;
            var width = tokens[end - 1].End - tokens[prefix].Start;
            // Bound both DP and traceback, including ordinal comparisons of long tokens.
            var work = 4 * slots + 2 * Math.Min(oldWidth * count, width * oldCount);
            if (work + 256 + 8 * chars > MaxCellWork || !Spend(work, ref budget)) return Coarse(oldText, text, ref budget);

            lengths = new ushort[slots];
            for (var i = oldCount - 1; i >= 0; --i)
            for (var j = count - 1; j >= 0; --j)
            {
                var slot = i * stride + j;
                lengths[slot] = Equal(prefix + i, prefix + j)
                    ? (ushort)(1 + lengths[slot + stride + 1])
                    : Math.Max(lengths[slot + stride], lengths[slot + 1]);
            }
        }

        var anchors = new List<Anchor>();
        if (prefix > 0) anchors.Add(new(0, 0, prefix));
        void Match(int oldIndex, int index)
        {
            if (anchors.Count > 0 && anchors[^1] is var last && last.Before + last.Count == oldIndex && last.After + last.Count == index)
                anchors[^1] = last with { Count = last.Count + 1 };
            else anchors.Add(new(oldIndex, index, 1));
        }

        if (lengths is not null)
        {
            var i = 0;
            var j = 0;
            while (i < oldCount && j < count)
            {
                if (Equal(prefix + i, prefix + j))
                {
                    Match(prefix + i, prefix + j);
                    ++i;
                    ++j;
                }
                // On ties discard the older token first, for stable repeated-word alignment.
                else if (lengths[(i + 1) * stride + j] >= lengths[i * stride + j + 1]) ++i;
                else ++j;
            }
        }

        if (oldEnd < oldTokens.Count) anchors.Add(new(oldEnd, end, oldTokens.Count - oldEnd));

        var edits = new List<Edit>();
        var whitespace = false;
        int oldAt = 0, at = 0;
        bool Gap(int oldStop, int stop)
        {
            // Keep equal surrounding whitespace once, without treating it as an
            // alignment token. Never split a CRLF when moving an edge.
            int i = oldAt, j = at;
            while (i < oldStop && j < stop && char.IsWhiteSpace(oldText[i]) && oldText[i] == text[j]) { ++i; ++j; }
            if (SplitsUnit(oldText, i) || SplitsUnit(text, j)) { --i; --j; }
            int k = oldStop, l = stop;
            while (k > i && l > j && char.IsWhiteSpace(oldText[k - 1]) && oldText[k - 1] == text[l - 1]) { --k; --l; }
            if (SplitsUnit(oldText, k) || SplitsUnit(text, l)) { ++k; ++l; }
            if (i == k && j == l) return true;
            if (edits.Count == MaxEdits) return false;
            edits.Add(new(new(i, k), new(j, l)));
            whitespace |= IsWhitespace(oldText.AsSpan(i, k - i)) && IsWhitespace(text.AsSpan(j, l - j));
            return true;
        }

        for (var a = 0; a < anchors.Count; ++a)
        {
            var anchor = anchors[a];
            var previous = a == 0 ? default : anchors[a - 1];
            var next = a + 1 == anchors.Count ? new Anchor(oldTokens.Count, tokens.Count, 0) : anchors[a + 1];
            // A lone token matched between two replacement gaps is usually incidental
            // prose/punctuation, not a useful break in the replacement. Keep edge anchors.
            if (anchor.Count == 1 && previous.Before + previous.Count < anchor.Before
                && previous.After + previous.Count < anchor.After
                && anchor.Before + 1 < next.Before && anchor.After + 1 < next.After) continue;
            for (var t = 0; t < anchor.Count; ++t)
            {
                var oldToken = oldTokens[anchor.Before + t];
                var token = tokens[anchor.After + t];
                if (!Gap(oldToken.Start, token.Start)) return Coarse(oldText, text, ref budget);
                oldAt = oldToken.End;
                at = token.End;
            }
        }
        if (!Gap(oldText.Length, text.Length)) return Coarse(oldText, text, ref budget);
        return new(edits.ToArray(), whitespace ? ChangeCue.Whitespace : ChangeCue.None);
    }

    private static bool Spend(int cost, ref int budget)
    {
        if (cost > budget) return false;
        budget -= cost;
        return true;
    }

    private static Result? Coarse(string before, string after, ref int budget)
    {
        var max = Math.Min(CoarseEdgeLimit, Math.Min(before.Length, after.Length));
        // Reserve the cue probe from the existing fixed part of the coarse charge.
        if (!Spend(CoarseCueLimit + 4 * max, ref budget)) return null;
        int prefix = 0, suffix = 0;
        for (var i = 0; i < max && before[i] == after[i]; ++i)
            if (SafeBoundary(before, i + 1) && SafeBoundary(after, i + 1)) prefix = i + 1;
        max = Math.Min(max, Math.Min(before.Length, after.Length) - prefix);
        for (var i = 1; i <= max && before[^i] == after[^i]; ++i)
        {
            if (SafeBoundary(before, before.Length - i) && SafeBoundary(after, after.Length - i)) suffix = i;
        }
        var oldRange = new Range(prefix, before.Length - suffix);
        var range = new Range(prefix, after.Length - suffix);
        var cue = ClassifyCoarse(before.AsSpan(oldRange.Start, oldRange.Length), after.AsSpan(range.Start, range.Length));
        return new([new(oldRange, range)], cue);
    }

    private static ChangeCue ClassifyCoarse(ReadOnlySpan<char> before, ReadOnlySpan<char> after)
    {
        var beforeCount = Math.Min(before.Length, CoarseCueLimit);
        var afterCount = Math.Min(after.Length, CoarseCueLimit - beforeCount);
        var whitespace = true;
        if (HasVisibleText(before[..beforeCount], ref whitespace) || HasVisibleText(after[..afterCount], ref whitespace))
            return ChangeCue.None;
        return whitespace && beforeCount == before.Length && afterCount == after.Length
            ? ChangeCue.Whitespace : ChangeCue.Unclassified;
    }

    private static bool HasVisibleText(ReadOnlySpan<char> sample, ref bool whitespace)
    {
        foreach (var ch in sample)
        {
            // An ASCII graphic is a conservative visible witness; do not assume
            // that arbitrary non-whitespace UTF-16 units have an inked glyph.
            if (ch is >= '!' and <= '~') return true;
            if (!char.IsWhiteSpace(ch)) whitespace = false;
        }
        return false;
    }

    private static bool SafeBoundary(string text, int at) => at == 0 || at == text.Length || !SplitsUnit(text, at)
        && (char.IsWhiteSpace(text[at - 1]) || char.IsWhiteSpace(text[at]) || char.IsPunctuation(text[at - 1]) || char.IsPunctuation(text[at]));

    private static bool SplitsUnit(string text, int at) => at > 0 && at < text.Length
        && (text[at - 1] == '\r' && text[at] == '\n' || char.IsHighSurrogate(text[at - 1]) && char.IsLowSurrogate(text[at]));

    private static bool IsWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var ch in text) if (!char.IsWhiteSpace(ch)) return false;
        return true;
    }

    private static List<Range> Tokenize(string text)
    {
        var tokens = new List<Range>();
        var start = -1;
        var at = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var end = at + rune.Utf16SequenceLength;
            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation)
            {
                if (start < 0) start = at;
            }
            else
            {
                if (start >= 0) { tokens.Add(new(start, at)); start = -1; }
                if (!Rune.IsWhiteSpace(rune)) tokens.Add(new(at, end));
            }
            at = end;
        }
        if (start >= 0) tokens.Add(new(start, at));
        return tokens;
    }
}
