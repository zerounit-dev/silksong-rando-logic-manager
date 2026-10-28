using System.Buffers;
using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Bounded lexical descriptors, derived only from the twenty-three approved choices.
/// No identity resolution or parser-result sharing between the independent parsers.</summary>
internal static class RequirementOperandSyntax
{
    internal sealed record Slot(string Role, bool Optional, bool ListCapable);

    internal static IReadOnlyList<Slot> Describe(RequirementInputSyntax syntax) =>
        RequirementCatalogueLanguage.ToPersistedValue(syntax).Split(' ').Select(marker =>
        {
            var list = marker.StartsWith('[');
            var name = marker.Trim('[', ']', '{', '}');
            var optional = name.EndsWith('?');
            return new Slot(name.TrimEnd('?'), optional, list);
        }).ToArray();

    internal static bool HasAlternatives(RequirementInputSyntax syntax) => Describe(syntax).Any(slot => slot.ListCapable);

    internal static bool HasComposedQuantity(RequirementInputSyntax syntax) => syntax is
        RequirementInputSyntax.PredicateThenOptionalQuantity or
        RequirementInputSyntax.PredicateThenDirectionThenOptionalQuantity or
        RequirementInputSyntax.PredicateThenOptionalDirectionThenOptionalQuantity or
        RequirementInputSyntax.RequiredDifficultyThenPredicateThenOptionalQuantity or
        RequirementInputSyntax.RequiredDifficultyThenPredicateThenQuantity or
        RequirementInputSyntax.RequiredDifficultyThenMethodThenPredicateThenOptionalQuantity;

    internal static bool UsesOperands(RequirementInputSyntax syntax) => HasComposedQuantity(syntax) || HasAlternatives(syntax);

    // Composed quantities only. Read the complete raw final slot, never identification-normalized digits
    // or punctuation-normalized markers. The parsers retain independent identity resolution.
    internal static bool TryQuantity(string raw, bool optional, out int quantity)
    {
        quantity = 1;
        var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return optional;
        var token = tokens[^1];
        return (tokens.Length == 1 || tokens.Length == 2 && tokens[0] is "x" or "X") &&
            token.Length > 0 && token[0] is >= '1' and <= '9' &&
            token.All(character => character is >= '0' and <= '9') &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out quantity);
    }

    // Brackets and commas are structural before identification, even on unmarked slots.
    internal static bool TryValues(string raw, Slot slot, out IReadOnlyList<string> values)
    {
        var text = raw.Trim();
        values = [];
        if (text.Length == 0) { if (!slot.Optional) return false; values = ["none"]; return true; }
        if (text.IndexOfAny(['[', ']', ',']) < 0) { values = [text]; return true; }
        if (!slot.ListCapable || !text.StartsWith('[') || !text.EndsWith(']')) return false;
        var entries = text[1..^1].Split(',').Select(value => value.Trim()).ToArray();
        if (entries.Any(value => value.Length == 0 || value.IndexOfAny(['[', ']', '(', ')']) >= 0 ||
            ContainsOperator(value))) return false;
        values = entries;
        return true;
    }

    private static bool ContainsOperator(string text)
    {
        for (var index = 0; index < text.Length; index++)
            foreach (var word in new[] { "AND", "OR" })
                if (index + word.Length <= text.Length && text.AsSpan(index, word.Length).SequenceEqual(word) &&
                    !IsIdentificationRuneBefore(text, index) && !IsIdentificationRuneAt(text, index + word.Length)) return true;
        return false;
    }

    private static bool IsIdentificationRuneBefore(string text, int index) => index > 0 &&
        Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var rune, out _) == OperationStatus.Done && IsIdentificationRune(rune);
    private static bool IsIdentificationRuneAt(string text, int index) => index < text.Length &&
        Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _) == OperationStatus.Done && IsIdentificationRune(rune);
    private static bool IsIdentificationRune(Rune rune) => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber;

    // Every whitespace boundary outside brackets participates. No longest/first alias rule.
    internal static IEnumerable<IReadOnlyList<string>> Partitions(string text, IReadOnlyList<Slot> slots)
    {
        text = text.Trim();
        var cuts = new List<int>();
        var bracket = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '[') { if (bracket) yield break; bracket = true; }
            else if (text[index] == ']') { if (!bracket) yield break; bracket = false; }
            if (!bracket && char.IsWhiteSpace(text[index]) && index > 0 && !char.IsWhiteSpace(text[index - 1])) cuts.Add(index);
        }
        if (bracket) yield break;
        foreach (var result in Split(0, 0, [])) yield return result;
        // Only the approved optional leading Crest shape supplies an aligned empty first slot.
        // Present partitions above remain complete candidates; do not guess away nonempty values.
        if (slots.Count == 3 && slots[0] == new Slot("crest", true, true) &&
            slots[1] == new Slot("predicate", false, false) && slots[2] == new Slot("direction", false, false))
            foreach (var result in Split(0, 1, [""])) yield return result;

        // Only the fifteenth descriptor admits an empty middle Crest. Every complete
        // difficulty/predicate boundary participates; no nonempty slice is discarded.
        if (slots.Count == 3 && slots[0] == new Slot("difficulty", false, false) &&
            slots[1] == new Slot("crest", true, true) && slots[2] == new Slot("predicate", false, false))
            foreach (var end in cuts)
            {
                var start = end;
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
                if (start < text.Length) yield return [text[..end], "", text[start..]];
            }

        // QTY2 alone permits both absent supporting slots, or an empty middle Direction
        // followed by a complete raw Quantity. Present Direction partitions above still compete.
        if (slots.Count == 3 && slots[0] == new Slot("predicate", false, false) &&
            slots[1] == new Slot("direction", true, true) && slots[2] == new Slot("quantity", true, false))
        {
            if (text.Length > 0 && RequirementCatalogueLanguage.NormalizeIdentification(
                text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]).Length != 0)
                yield return [text, "", ""];
            foreach (var end in cuts)
            {
                var start = end;
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
                if (start < text.Length) yield return [text[..end], "", text[start..]];
            }
        }

        IEnumerable<IReadOnlyList<string>> Split(int start, int slot, IReadOnlyList<string> prefix)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (slot == slots.Count - 1)
            {
                if (start < text.Length || slots[slot].Optional) yield return [.. prefix, text[start..]];
                yield break;
            }
            foreach (var end in cuts.Where(end => end > start))
                foreach (var result in Split(end, slot + 1, [.. prefix, text[start..end]])) yield return result;
            // Approved optional final Direction or QTY1 Quantity may be omitted.
            if (slot == slots.Count - 2 && slots[^1].Optional && start < text.Length &&
                RequirementCatalogueLanguage.NormalizeIdentification(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]).Length != 0)
                yield return [.. prefix, text[start..], ""];
        }
    }
}
