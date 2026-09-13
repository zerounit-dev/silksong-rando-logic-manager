using System.Buffers;
using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record RoomGraphPredicateDefinition(Guid Id, string Name, RequirementInputSyntax Syntax, string OutputSyntax, IReadOnlyList<string> Aliases);
internal sealed record RoomGraphItemDefinition(Guid Id, string Name, string OutputValue, IReadOnlyList<string> Aliases);
internal sealed record RoomGraphRequirementRoom(Guid Id, string ReferenceId);
internal sealed record RoomGraphRequirementCheck(Guid Id, Guid RoomId, string FriendlyName, string? LocationId);
internal sealed record RoomGraphTranslationContext(
    IReadOnlyList<RoomGraphPredicateDefinition> Predicates,
    IReadOnlyList<RoomGraphItemDefinition> Items,
    IReadOnlyList<RoomGraphRequirementRoom> Rooms,
    IReadOnlyList<RoomGraphRequirementCheck> Checks);

internal static class RoomGraphRequirementTranslator
{
    internal static RoomGraphRequirement Translate(string source, Guid owningRoomId, RoomGraphTranslationContext context)
    {
        if (string.IsNullOrWhiteSpace(source)) return Unresolved(source, "requirement is blank");
        var parser = new Parser(source, owningRoomId, context);
        var parsed = parser.Parse();
        if (parsed.Node is null) return new(source, "unresolved", [], parsed.Reasons);

        var dnf = Lower(parsed.Node);
        if (parsed.Node is AtomNode { Value: "none" }) return new(source, "none", [[]], []);
        return new(source, "expression", dnf, []);
    }

    private static RoomGraphRequirement Unresolved(string raw, string reason) => new(raw, "unresolved", [], [reason]);

    private static IReadOnlyList<IReadOnlyList<string>> Lower(Node node) => node switch
    {
        AtomNode atom => [[atom.Value]],
        OrNode or => [.. Lower(or.Left), .. Lower(or.Right)],
        AndNode and => Lower(and.Left).SelectMany(left => Lower(and.Right).Select(right =>
            (IReadOnlyList<string>)[.. left, .. right])).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(node))
    };

    private abstract record Node;
    private sealed record AtomNode(string Value) : Node;
    private sealed record AndNode(Node Left, Node Right) : Node;
    private sealed record OrNode(Node Left, Node Right) : Node;
    private sealed record ParseResult(Node? Node, IReadOnlyList<string> Reasons);
    private sealed record AtomCandidate(string? Output, string? Failure);

    private sealed class Parser(string source, Guid owningRoomId, RoomGraphTranslationContext context)
    {
        private static readonly HashSet<string> Difficulties = new(StringComparer.Ordinal) { "easy", "medium", "hard" };
        private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "left", "right", "up", "down" };
        private int position;
        private readonly List<string> reasons = [];

        internal ParseResult Parse()
        {
            SkipWhitespace();
            var node = ParseExpression(false);
            SkipWhitespace();
            if (node is not null && position != source.Length)
            {
                reasons.Add(source[position] == ')' ? "unmatched closing parenthesis" : $"unexpected text near: {source[position..].Trim()}");
                node = null;
            }
            return new(node, reasons.Count == 0 ? [] : reasons);
        }

        private Node? ParseExpression(bool grouped)
        {
            var left = ParseOperand();
            if (left is null) return null;
            string? operatorKind = null;
            while (true)
            {
                SkipWhitespace();
                if (position >= source.Length)
                {
                    if (grouped) reasons.Add("mismatched parentheses");
                    return grouped ? null : left;
                }
                if (source[position] == ')')
                {
                    if (!grouped) return left;
                    position++;
                    return left;
                }

                var next = WordAt("AND") ? "AND" : WordAt("OR") ? "OR" : null;
                if (next is null)
                {
                    reasons.Add($"missing Boolean operator near: {source[position..].Trim()}");
                    return null;
                }
                if (operatorKind is not null && operatorKind != next)
                {
                    reasons.Add("mixed AND and OR require parentheses");
                    return null;
                }
                operatorKind = next;
                position += next.Length;
                SkipWhitespace();
                if (position >= source.Length || source[position] == ')')
                {
                    reasons.Add($"Boolean operator {next} is missing its right operand");
                    return null;
                }
                var right = ParseOperand();
                if (right is null) return null;
                left = next == "AND" ? new AndNode(left, right) : new OrNode(left, right);
            }
        }

        private Node? ParseOperand()
        {
            SkipWhitespace();
            if (position >= source.Length)
            {
                reasons.Add("requirement expression is incomplete");
                return null;
            }
            if (source[position] == ')')
            {
                reasons.Add("empty Boolean group or missing operand");
                return null;
            }
            if (source[position] == '(')
            {
                position++;
                return ParseExpression(true);
            }

            var start = position;
            while (position < source.Length && source[position] is not '(' and not ')' && !WordAt("AND") && !WordAt("OR")) position++;
            var raw = source[start..position].Trim();
            var atom = TranslateAtom(raw);
            if (atom is null) return null;

            SkipWhitespace();
            if (position < source.Length && source[position] == '(' && !ParseAnnotation()) return null;
            return new AtomNode(atom);
        }

        private bool ParseAnnotation()
        {
            position++;
            var start = position;
            while (position < source.Length && source[position] != ')')
            {
                if (source[position] is '(' or '\\')
                {
                    reasons.Add("annotation must be plain, nonnested text without backslash escaping");
                    return false;
                }
                position++;
            }
            if (position >= source.Length)
            {
                reasons.Add("mismatched annotation parenthesis");
                return false;
            }
            if (string.IsNullOrWhiteSpace(source[start..position]))
            {
                reasons.Add("annotation is blank");
                return false;
            }
            position++;
            return true;
        }

        private string? TranslateAtom(string raw)
        {
            if (raw.Length == 0)
            {
                reasons.Add("requirement atom is blank");
                return null;
            }
            var normalized = RequirementCatalogueLanguage.NormalizeIdentification(raw);
            var candidates = new List<AtomCandidate>();
            foreach (var predicate in context.Predicates)
                foreach (var alias in predicate.Aliases)
                    AddCandidate(predicate, alias, raw, normalized, candidates);

            var successful = candidates.Where(candidate => candidate.Output is not null && candidate.Failure is null).ToArray();
            if (successful.Length == 1) return successful[0].Output!;
            if (successful.Length > 1)
            {
                reasons.Add($"ambiguous requirement atom: {raw}");
                return null;
            }
            if (candidates.Count == 0)
            {
                reasons.Add($"unrecognized predicate: {raw}");
                return null;
            }
            foreach (var candidate in candidates)
                if (candidate.Failure is { } failure) reasons.Add(failure);
            return null;
        }

        private void AddCandidate(RoomGraphPredicateDefinition predicate, string alias, string raw, string normalized, List<AtomCandidate> candidates)
        {
            var normalizedAlias = RequirementCatalogueLanguage.NormalizeIdentification(alias);
            RequirementAtomFormatArguments arguments = new();
            string? failure = null;
            var matched = false;
            switch (predicate.Syntax)
            {
                case RequirementInputSyntax.PredicateOnly:
                    matched = normalized == normalizedAlias;
                    break;
                case RequirementInputSyntax.OptionalDifficultyThenPredicate:
                    matched = normalized == normalizedAlias;
                    if (!matched && TryLeading(normalized, normalizedAlias, out var optionalDifficulty))
                    {
                        matched = true;
                        if (Difficulties.Contains(optionalDifficulty)) arguments = arguments with { Difficulty = optionalDifficulty };
                        else failure = $"invalid modifier: {optionalDifficulty}";
                    }
                    break;
                case RequirementInputSyntax.RequiredDifficultyThenPredicate:
                    if (normalized == normalizedAlias) { matched = true; failure = $"required difficulty is missing for: {raw}"; }
                    else if (TryLeading(normalized, normalizedAlias, out var requiredDifficulty))
                    {
                        matched = true;
                        if (Difficulties.Contains(requiredDifficulty)) arguments = arguments with { Difficulty = requiredDifficulty };
                        else failure = $"invalid modifier: {requiredDifficulty}";
                    }
                    break;
                case RequirementInputSyntax.PredicateThenDirection:
                    if (normalized == normalizedAlias) { matched = true; failure = $"required direction is missing for: {raw}"; }
                    else if (TryRemainder(normalized, normalizedAlias, out var direction))
                    {
                        matched = true;
                        if (Directions.Contains(direction)) arguments = arguments with { Direction = direction };
                        else failure = $"invalid direction: {direction}";
                    }
                    break;
                case RequirementInputSyntax.PredicateThenItem:
                    if (normalized == normalizedAlias) { matched = true; failure = $"required item is missing for: {raw}"; }
                    else if (TryRemainder(normalized, normalizedAlias, out var itemText))
                    {
                        matched = true;
                        var items = context.Items.Where(item => item.Aliases.Any(value => RequirementCatalogueLanguage.NormalizeIdentification(value) == itemText)).ToArray();
                        if (items.Length == 1) arguments = arguments with { ItemOutputValue = items[0].OutputValue };
                        else failure = items.Length == 0 ? $"unrecognized item: {itemText}" : $"ambiguous item: {itemText}";
                    }
                    break;
                case RequirementInputSyntax.PredicateThenQuantity:
                    if (normalized == normalizedAlias) { matched = true; failure = $"required quantity is missing for: {raw}"; }
                    else if (TryRemainder(normalized, normalizedAlias, out var quantityText))
                    {
                        matched = true;
                        if (quantityText.Length > 0 && quantityText.All(value => value is >= '0' and <= '9') &&
                            int.TryParse(quantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) && quantity > 0)
                            arguments = arguments with { Quantity = quantity };
                        else failure = $"invalid positive quantity: {quantityText}";
                    }
                    break;
                case RequirementInputSyntax.PredicateThenCheck:
                    var prefix = PrefixLength(raw, alias);
                    if (prefix >= 0)
                    {
                        matched = true;
                        var dependency = ResolveDependency(raw[prefix..]);
                        arguments = arguments with { CheckGraphId = dependency.LocationId };
                        failure = dependency.Failure;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            if (!matched) return;
            if (failure is not null) { candidates.Add(new(null, failure)); return; }
            try { candidates.Add(new(RequirementAtomFormatter.Format(predicate.Syntax, predicate.OutputSyntax, arguments), null)); }
            catch (Exception exception) { candidates.Add(new(null, $"predicate '{predicate.Name}' could not format its output: {exception.Message}")); }
        }

        private (string? LocationId, string? Failure) ResolveDependency(string value)
        {
            var operand = value.TrimStart();
            if (operand.Length == 0) return (null, "required check name is missing");
            var qualifiers = FindQualifiers(operand);
            if (qualifiers.Any(value => value.Word == "GLOBAL")) return (null, "GLOBAL is not supported; use THE for full-catalogue scope");
            Guid? targetRoom = owningRoomId;
            string checkName;
            if (qualifiers.Count == 0) checkName = operand;
            else if (qualifiers.Count == 1 && qualifiers[0].Word == "THE" && qualifiers[0].Index == 0)
            {
                targetRoom = null;
                checkName = operand[3..].TrimStart();
            }
            else if (qualifiers.Count == 1 && qualifiers[0].Word == "IN" && qualifiers[0].Index > 0)
            {
                checkName = operand[..qualifiers[0].Index].TrimEnd();
                var roomText = operand[(qualifiers[0].Index + 2)..].TrimStart();
                if (checkName.Length == 0 || roomText.Length == 0) return (null, "check dependency has an incomplete IN scope");
                var roomIdentity = RequirementCatalogueLanguage.NormalizeIdentification(roomText);
                var rooms = context.Rooms.Where(room => RequirementCatalogueLanguage.NormalizeIdentification(room.ReferenceId) == roomIdentity).ToArray();
                if (rooms.Length != 1) return (null, rooms.Length == 0 ? $"dependency room was not found: {roomText}" : $"dependency room is ambiguous: {roomText}");
                targetRoom = rooms[0].Id;
            }
            else return (null, "check dependency has invalid or conflicting scope syntax");

            if (string.IsNullOrWhiteSpace(checkName)) return (null, "required check name is missing");
            var identity = RequirementCatalogueLanguage.NormalizeIdentification(checkName);
            var checks = context.Checks.Where(check => (targetRoom is null || check.RoomId == targetRoom) &&
                RequirementCatalogueLanguage.NormalizeIdentification(check.FriendlyName) == identity).ToArray();
            if (checks.Length != 1) return (null, checks.Length == 0 ? $"dependency check was not found: {checkName}" : $"dependency check is ambiguous: {checkName}");
            return checks[0].LocationId is { } id ? (id, null) : (null, $"dependency check has no valid export location identity: {checkName}");
        }

        private static List<(string Word, int Index)> FindQualifiers(string text)
        {
            var result = new List<(string, int)>();
            for (var index = 0; index < text.Length; index++)
                foreach (var word in new[] { "IN", "THE", "GLOBAL" })
                    if (WordAt(text, word, index)) { result.Add((word, index)); index += word.Length - 1; break; }
            return result;
        }

        private static bool TryLeading(string value, string alias, out string leading)
        {
            var suffix = " " + alias;
            if (value.EndsWith(suffix, StringComparison.Ordinal)) { leading = value[..^suffix.Length]; return leading.Length > 0; }
            leading = string.Empty;
            return false;
        }

        private static bool TryRemainder(string value, string alias, out string remainder)
        {
            var prefix = alias + " ";
            if (value.StartsWith(prefix, StringComparison.Ordinal)) { remainder = value[prefix.Length..]; return true; }
            remainder = string.Empty;
            return false;
        }

        private static int PrefixLength(string text, string alias)
        {
            var target = RequirementCatalogueLanguage.NormalizeIdentification(alias);
            for (var end = 1; end <= text.Length; end++)
                if (RequirementCatalogueLanguage.NormalizeIdentification(text[..end]) == target &&
                    (end == text.Length || char.IsWhiteSpace(text[end]))) return end;
            return -1;
        }

        private bool WordAt(string word) => WordAt(source, word, position);
        private static bool WordAt(string text, string word, int index) => index + word.Length <= text.Length &&
            text.AsSpan(index, word.Length).SequenceEqual(word) && !IsIdentificationRuneBefore(text, index) && !IsIdentificationRuneAt(text, index + word.Length);
        private static bool IsIdentificationRuneBefore(string text, int index) => index > 0 && Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var rune, out _) == OperationStatus.Done && IsIdentificationRune(rune);
        private static bool IsIdentificationRuneAt(string text, int index) => index < text.Length && Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _) == OperationStatus.Done && IsIdentificationRune(rune);
        private static bool IsIdentificationRune(Rune rune) => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber;
        private void SkipWhitespace() { while (position < source.Length && char.IsWhiteSpace(source[position])) position++; }
    }
}
