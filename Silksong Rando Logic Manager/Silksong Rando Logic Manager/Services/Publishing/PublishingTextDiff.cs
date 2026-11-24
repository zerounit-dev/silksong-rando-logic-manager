using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

// Comparison authority: exact decoded UTF-16 slices, independent of display rows.
public sealed class PublishingTextDiff
{
    internal enum Kind { Shared, Removed, Inserted }
    internal readonly record struct Span(Kind Kind, int Old, int New, int Length);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MinGlobalContext = 12, MaxContext = 128, LocalCharacters = 2_048;

    internal string? Before { get; }
    internal string? After { get; }
    internal bool BeforePresent { get; }
    internal bool AfterPresent { get; }
    internal bool Available => Before is not null && After is not null;
    internal string? Notice { get; }
    internal IReadOnlyList<Span> Spans { get; }
    internal PublishingDiffProjection Projection { get; }
    internal static bool IsTextPath(string path) => path.EndsWith(".md", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal);

    private PublishingTextDiff(string? before, string? after, bool beforePresent, bool afterPresent, string? notice)
    {
        (Before, After, BeforePresent, AfterPresent, Notice) = (before, after, beforePresent, afterPresent, notice);
        Spans = Available ? CompareText(before!, after!).AsReadOnly() : Array.Empty<Span>();
        Projection = new(this);
    }

    internal static PublishingTextDiff Compare(byte[]? oldBytes, byte[]? newBytes, PublishingTimingScope timing)
    {
        var before = TryDecode(oldBytes);
        var after = TryDecode(newBytes);
        string? notice = null;
        if (before is null || after is null)
            notice = before is null && after is null ? "Comparison unavailable: neither side is valid UTF-8 or BOM-marked Unicode."
                : before is null ? "Comparison unavailable: the Git base cannot be decoded. " + ReadableSide(after!, newBytes, "Candidate")
                : "Comparison unavailable: the candidate cannot be decoded. " + ReadableSide(before, oldBytes, "Git base");
        else if (before == after)
            notice = oldBytes is null ? "Added with no visible text." : newBytes is null ? "Removed with no visible text."
                : "The file bytes changed without a visible text difference (for example, an encoding marker).";
        else if (oldBytes is not null && newBytes is not null && Preamble(oldBytes) != Preamble(newBytes))
            notice = "The file's encoding marker also changed.";

        if (before is not null && (after is not null || oldBytes is not null))
            timing.BeforeLines = (timing.BeforeLines ?? 0) + CountLines(before);
        if (after is not null && (before is not null || newBytes is not null))
            timing.AfterLines = (timing.AfterLines ?? 0) + CountLines(after);
        return new(before, after, oldBytes is not null, newBytes is not null, notice);

        static string ReadableSide(string content, byte[]? bytes, string name) => bytes is null ? $"{name} absent."
            : content.Length == 0 ? $"{name} has no visible text." : $"Readable {name} shown without comparison.";
    }

    private static int CountLines(string text)
    {
        int count = 0;
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            count++;
            int i = rest.IndexOfAny('\r', '\n');
            if (i < 0) break;
            rest = rest[(i + EndingLength(rest, i))..];
        }
        return count;
    }

    internal static int EndingLength(ReadOnlySpan<char> text, int i) => text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;

    private static List<Span> CompareText(string before, string after)
    {
        List<Span> result = [];
        if (before == after)
        {
            Add(Kind.Shared, 0, 0, before.Length);
            return result;
        }

        var (oldAt, oldEnd, newAt, newEnd) = TrimEdges(0, before.Length, 0, after.Length);
        bool twoSidedGap = oldAt < oldEnd && newAt < newEnd;
        List<Anchor> shortFences = twoSidedGap ? Witnesses(before, 0, before.Length, after, 0, after.Length, 0) : [];
        List<Anchor> phraseFences = twoSidedGap ? Witnesses(before, 0, before.Length, after, 0, after.Length, 3) : [];
        var fences = Ordered(shortFences, oldAt, oldEnd, newAt, newEnd);
        foreach (var anchor in fences)
        {
            Refine(oldAt, anchor.Old, newAt, anchor.New, level: 0, fenced: true);
            Add(Kind.Shared, anchor.Old, anchor.New, anchor.Length);
            oldAt = anchor.Old + anchor.Length;
            newAt = anchor.New + anchor.Length;
        }
        Refine(oldAt, oldEnd, newAt, newEnd, level: 0, fenced: fences.Count > 0);
        Add(Kind.Shared, oldEnd, newEnd, before.Length - oldEnd);
        return result;

        void Add(Kind kind, int x, int y, int length)
        {
            if (length == 0) return;
            if (result.Count > 0 && result[^1] is var previous && previous.Kind == kind
                && (x < 0 || previous.Old + previous.Length == x)
                && (y < 0 || previous.New + previous.Length == y))
                result[^1] = previous with { Length = previous.Length + length };
            else result.Add(new(kind, x, y, length));
        }

        (int A, int Z, int B, int W) TrimEdges(int x, int endX, int y, int endY)
        {
            var fromX = x; var fromY = y;
            while (x < endX && y < endY && before[x] == after[y])
            {
                int nextX = Next(before, x, endX), nextY = Next(after, y, endY);
                if (nextX - x != nextY - y || !before.AsSpan(x, nextX - x).SequenceEqual(after.AsSpan(y, nextY - y))) break;
                (x, y) = (nextX, nextY);
            }
            Add(Kind.Shared, fromX, fromY, x - fromX);
            while (x < endX && y < endY && before[endX - 1] == after[endY - 1])
            {
                int prevX = Previous(before, x, endX), prevY = Previous(after, y, endY);
                if (endX - prevX != endY - prevY || !before.AsSpan(prevX, endX - prevX).SequenceEqual(after.AsSpan(prevY, endY - prevY))) break;
                (endX, endY) = (prevX, prevY);
            }
            return (x, endX, y, endY);
        }

        void Refine(int x, int endX, int y, int endY, int level, bool fenced)
        {
            var (a, z, b, w) = TrimEdges(x, endX, y, endY);
            if (a < z && b < w && (level == 0 || fenced && level < 4))
            {
                // Global phrases may be used only inside the already ordered
                // distinct fences. Shorter/repeated context is considered only
                // inside bounded, fenced intervals. Fixed depth and disjoint
                // gaps keep the work proportional without a shared budget.
                var anchors = level == 0 ? phraseFences
                    : (long)(z - a) + (w - b) <= LocalCharacters ? Witnesses(before, a, z, after, b, w, 4 - level) : [];
                var ordered = Ordered(anchors, a, z, b, w);
                int next = level + 1;
                foreach (var anchor in ordered)
                {
                    Refine(a, anchor.Old, b, anchor.New, next, fenced: true);
                    Add(Kind.Shared, anchor.Old, anchor.New, anchor.Length);
                    a = anchor.Old + anchor.Length;
                    b = anchor.New + anchor.Length;
                }
                Refine(a, z, b, w, next, fenced || ordered.Count > 0);
            }
            else
            {
                Add(Kind.Removed, a, -1, z - a);
                Add(Kind.Inserted, -1, b, w - b);
            }
            Add(Kind.Shared, z, w, endX - z);
        }
    }

    private readonly record struct Anchor(int Old, int New, int Length);
    private readonly record struct Witness(int Word, int Length, int Start, int End);
    private readonly record struct Occurrence(int Old, int New); // +1 offset, 0 absent, -1 repeated.

    // Count both complete sources for global ordering, before using any match.
    // Only exact-equal witness slices enter this list; no normalized text or
    // fingerprint can establish equality for a shared span.
    private static List<Anchor> Witnesses(string before, int a, int z, string after, int b, int w, int depth)
    {
        if (a == z || b == w) return [];
        var index = new Dictionary<ReadOnlyMemory<char>, Occurrence>(OrdinalMemory.Instance);
        foreach (var c in Contexts(before, a, z, depth))
        {
            var key = before.AsMemory(c.Start, c.End - c.Start);
            if (!index.TryAdd(key, new(c.Word + 1, 0))) index[key] = new(-1, 0);
        }
        foreach (var c in Contexts(after, b, w, depth))
        {
            var key = after.AsMemory(c.Start, c.End - c.Start);
            if (index.TryGetValue(key, out var seen) && seen.Old > 0 && seen.New >= 0)
                index[key] = seen with { New = seen.New == 0 ? c.Word + 1 : -1 };
        }
        List<Anchor> matches = [];
        foreach (var c in Contexts(before, a, z, depth))
        {
            var seen = index[before.AsMemory(c.Start, c.End - c.Start)];
            if (seen.Old <= 0 || seen.New <= 0) continue;
            matches.Add(new(c.Word, seen.New - 1, c.Length));
        }
        return matches;
    }

    private static List<Anchor> Ordered(List<Anchor> matches, int a, int z, int b, int w)
    {
        List<Anchor> candidates = [];
        int lo = 0, hi = matches.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (matches[mid].Old < a) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < matches.Count && matches[i].Old < z; i++)
        {
            var c = matches[i];
            if (c.Old + c.Length > z || c.New < b || c.New + c.Length > w) continue;
            // Occurrence counting is already complete. Overlapping evidence on
            // the same diagonal agrees about every shared character; combine
            // those emitted ranges, not the (possibly longer) witness keys.
            if (candidates.Count > 0 && candidates[^1] is var prior && c.Old < prior.Old + prior.Length
                && c.New - prior.New == c.Old - prior.Old)
                candidates[^1] = prior with { Length = Math.Max(prior.Length, c.Old - prior.Old + c.Length) };
            else candidates.Add(c);
        }
        // Reject both sides of any remaining overlap or order conflict. Keep
        // even rejected candidates in the frontiers so no rival wins by order.
        int oldEnd = a, newEnd = b;
        var ordered = new bool[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            ordered[i] = c.Old >= oldEnd && c.New >= newEnd;
            oldEnd = Math.Max(oldEnd, c.Old + c.Length);
            newEnd = Math.Max(newEnd, c.New + c.Length);
        }
        int oldStart = z, newStart = w;
        List<Anchor> result = [];
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            var c = candidates[i];
            if (ordered[i] && c.Old + c.Length <= oldStart && c.New + c.Length <= newStart) result.Add(c);
            oldStart = Math.Min(oldStart, c.Old);
            newStart = Math.Min(newStart, c.New);
        }
        result.Reverse();
        return result;
    }

    private static IEnumerable<Witness> Contexts(string text, int first, int end, int depth)
    {
        if (depth == 0)
        {
            // Short literal fences: punctuation-adjacent phrases (no grammar,
            // file-type, route or row identity). The next punctuation/ending
            // only terminates a witness; CRLF vs LF cannot falsify its text.
            for (int i = first; i < end;)
            {
                int next = Next(text, i, end);
                var rune = Rune.GetRuneAt(text, i);
                if (IsWord(rune) && next - i is >= 5 and < MaxContext && next < end
                    && Rune.IsPunctuation(Rune.GetRuneAt(text, next)))
                {
                    int stop = Next(text, next, end);
                    // Establish the short clause's identity without pulling the
                    // word being replaced (or its length) into the witness.
                    yield return new(i, next - i, i, stop);
                }
                else if (Rune.IsPunctuation(rune) || Rune.IsSymbol(rune))
                {
                    int cursor = next, wordEnd = next, words = 0;
                    while (cursor < end && cursor - i <= MaxContext)
                    {
                        if (text[cursor] is ' ' or '\t') { cursor++; continue; }
                        var letter = Rune.GetRuneAt(text, cursor);
                        if (!IsWord(letter) || ++words > 3) break;
                        // Inspect only the bounded prefix of a long word. If
                        // it exceeds the key limit this whole start is ineligible.
                        do { cursor += letter.Utf16SequenceLength; }
                        while (cursor < end && cursor - i <= MaxContext && IsWord(letter = Rune.GetRuneAt(text, cursor)));
                        wordEnd = cursor;
                    }
                    if (words is > 0 and <= 3 && wordEnd - i is >= 5 and <= MaxContext && cursor - i <= MaxContext)
                    {
                        int stop = wordEnd;
                        if (cursor < end && Rune.IsPunctuation(Rune.GetRuneAt(text, cursor)))
                        {
                            int afterPunctuation = Next(text, cursor, end);
                            if (afterPunctuation - i <= MaxContext) stop = afterPunctuation;
                        }
                        yield return new(i, wordEnd - i, i, stop);
                    }
                }
                // Advance by one token, never past a possible overlapping
                // witness. Only this outer scan walks an unbounded word.
                i = next;
            }
            yield break;
        }

        // Three passes over the changed middle, at most 128 literal chars per
        // indexed key, no substring copies or whole-file edit matrix.
        int previous = -1, last = -1, prevEnd = 0, lastEnd = 0;
        int priorToken = first;
        for (int i = first; i < end;)
        {
            int next = Next(text, i, end);
            if (IsWord(Rune.GetRuneAt(text, i)))
            {
                if (depth == 1)
                {
                    // A local lone word needs literal context on both sides.
                    // Never match standalone punctuation/indentation as an island.
                    if (next - i >= 3 && i > first && next < end)
                    {
                        int following = Next(text, next, end);
                        if (following - priorToken <= MaxContext)
                            yield return new(i, next - i, priorToken, following);
                    }
                }
                else
                {
                    var start = depth == 2 ? last : previous;
                    var length = depth == 2 ? lastEnd - last : prevEnd - previous;
                    if (start >= 0 && next - start <= MaxContext && (depth < 3 || next - start >= MinGlobalContext))
                        yield return new(start, length, start, next);
                }
                (previous, prevEnd, last, lastEnd) = (last, lastEnd, i, next);
            }
            priorToken = i;
            i = next;
        }
    }

    private static bool IsWord(Rune rune) => rune.Value == '_' || Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    private static int Next(string text, int i, int end)
    {
        if (text[i] == '\r') return i + EndingLength(text.AsSpan(0, end), i);
        var rune = Rune.GetRuneAt(text, i);
        i += rune.Utf16SequenceLength;
        if (IsWord(rune))
            while (i < end && IsWord(rune = Rune.GetRuneAt(text, i))) i += rune.Utf16SequenceLength;
        return i;
    }

    private static int Previous(string text, int start, int end)
    {
        int i = end - 1;
        if (text[i] == '\n' && i > start && text[i - 1] == '\r') return i - 1;
        if (char.IsLowSurrogate(text[i])) i--;
        if (IsWord(Rune.GetRuneAt(text, i)))
            while (i > start)
            {
                int j = i - (char.IsLowSurrogate(text[i - 1]) ? 2 : 1);
                if (!IsWord(Rune.GetRuneAt(text, j))) break;
                i = j;
            }
        return i;
    }

    private sealed class OrdinalMemory : IEqualityComparer<ReadOnlyMemory<char>>
    {
        internal static readonly OrdinalMemory Instance = new();
        public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y) => x.Span.SequenceEqual(y.Span);
        public int GetHashCode(ReadOnlyMemory<char> value) => string.GetHashCode(value.Span, StringComparison.Ordinal);
    }

    private static int Preamble(ReadOnlySpan<byte> bytes) => bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) ? 4
        : bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) ? 5
        : bytes.StartsWith(new byte[] { 0xff, 0xfe }) ? 2 : bytes.StartsWith(new byte[] { 0xfe, 0xff }) ? 3
        : bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 1 : 0;

    private static string? TryDecode(byte[]? bytes)
    {
        try
        {
            return bytes is null ? "" : Preamble(bytes) switch
            {
                1 => Utf8.GetString(bytes.AsSpan(3)),
                2 => new UnicodeEncoding(false, false, true).GetString(bytes.AsSpan(2)),
                3 => new UnicodeEncoding(true, false, true).GetString(bytes.AsSpan(2)),
                4 => new UTF32Encoding(false, false, true).GetString(bytes.AsSpan(4)),
                5 => new UTF32Encoding(true, false, true).GetString(bytes.AsSpan(4)),
                _ => Utf8.GetString(bytes)
            };
        }
        catch (DecoderFallbackException) { return null; }
    }
}
