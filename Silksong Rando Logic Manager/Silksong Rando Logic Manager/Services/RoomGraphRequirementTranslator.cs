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
    IReadOnlyList<RoomGraphRequirementCheck> Checks,
    IReadOnlyList<RoomGraphItemDefinition> Countables,
    IReadOnlyList<RoomGraphItemDefinition> Souls,
    IReadOnlyList<RoomGraphItemDefinition> Crests,
    IReadOnlyList<RoomGraphItemDefinition> Methods);

internal static class RoomGraphRequirementTranslator
{
    internal static AuditRequirement RecognizeForAudit(string source, Guid roomId, RoomGraphTranslationContext context)
    {
        if (string.IsNullOrWhiteSpace(source)) return new(source, null, ["requirement is blank"]);
        var parsed = new Parser(source, roomId, context).Parse();
        return new(source, parsed.Node is null ? null : Project(parsed.Node), parsed.Reasons);
    }

    private static AuditExpression Project(Node node) => node switch
    {
        AtomNode { Evidence: { } evidence } => evidence,
        AndNode and => new AuditBoolean(true, Project(and.Left), Project(and.Right)),
        OrNode or => new AuditBoolean(false, Project(or.Left), Project(or.Right)),
        _ => throw new InvalidOperationException("Successful recognition has no audit evidence.")
    };
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
    private sealed record AtomNode(string Value, AuditAtom? Evidence = null) : Node;
    private sealed record AndNode(Node Left, Node Right) : Node;
    private sealed record OrNode(Node Left, Node Right) : Node;
    private sealed record ParseResult(Node? Node, IReadOnlyList<string> Reasons);
    private sealed record AtomCandidate(Node? Output, string? Failure);

    private sealed class Parser(string source, Guid owningRoomId, RoomGraphTranslationContext context)
    {
        private static readonly HashSet<string> Difficulties = new(StringComparer.Ordinal) { "easy", "medium", "hard", "cbt", "intended" };
        private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "left", "right", "up", "down", "both", "either", "any", "all" };
        private int position;
        private int atomStart;
        private int atomLength;
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
            var bracketDepth = 0;
            while (position < source.Length)
            {
                var character = source[position];
                if (bracketDepth == 0 && (character is '(' or ')' || WordAt("AND") || WordAt("OR"))) break;
                if (character == '[') bracketDepth++;
                if (character == ']') bracketDepth--;
                if (bracketDepth < 0) { reasons.Add("unmatched list delimiter"); return null; }
                position++;
            }
            if (bracketDepth != 0) { reasons.Add("unmatched list delimiter"); return null; }
            var raw = source[start..position].Trim();
            atomStart = start;
            atomLength = raw.Length;
            var atom = TranslateAtom(raw);
            if (atom is null) return null;

            SkipWhitespace();
            if (position < source.Length && source[position] == '(' && !ParseAnnotation()) return null;
            return atom;
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

        private Node? TranslateAtom(string raw)
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
                reasons.Add(raw.IndexOfAny(['[', ']', ',']) >= 0
                    ? $"invalid or unrecognized operand-list application (check selected list markers and operand order): {raw}"
                    : $"unrecognized predicate: {raw}");
                return null;
            }
            foreach (var candidate in candidates)
                if (candidate.Failure is { } failure) reasons.Add(failure);
            return null;
        }

        private void AddCandidate(RoomGraphPredicateDefinition predicate, string alias, string raw, string normalized, List<AtomCandidate> candidates)
        {
            if (RequirementOperandSyntax.UsesOperands(predicate.Syntax))
            {
                AddOperandCandidates(predicate, alias, raw, candidates);
                return;
            }
            if (raw.IndexOfAny(['[', ']', ',']) >= 0) return;
            var normalizedAlias = RequirementCatalogueLanguage.NormalizeIdentification(alias);
            RequirementAtomFormatArguments arguments = new();
            var operands = new List<AuditOperand>();
            Guid? dependencyId = null;
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
                case RequirementInputSyntax.PredicateThenItem:
                    if (normalized == normalizedAlias) { matched = true; failure = $"required item is missing for: {raw}"; }
                    else if (TryRemainder(normalized, normalizedAlias, out var itemText))
                    {
                        matched = true;
                        var items = context.Items.Where(item => item.Aliases.Any(value => RequirementCatalogueLanguage.NormalizeIdentification(value) == itemText)).ToArray();
                        if (items.Length == 1) { arguments = arguments with { ItemOutputValue = items[0].OutputValue }; operands.Add(new("item", items[0].Id, items[0].OutputValue, raw)); }
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
                        dependencyId = dependency.CheckId;
                        failure = dependency.Failure;
                    }
                    break;
                case RequirementInputSyntax.PredicateThenQuantityThenCountable:
                    var countPrefix = PrefixLength(raw, alias);
                    if (countPrefix >= 0)
                    {
                        matched = true;
                        if (!RequirementTypedQuantity.TryRead(raw[countPrefix..], out var count, out var countableText))
                            failure = $"a canonical positive Int32 quantity and countable are required for: {raw}";
                        else
                        {
                            var identity = RequirementCatalogueLanguage.NormalizeIdentification(countableText);
                            var countables = context.Countables.Where(countable => countable.Aliases.Any(value => RequirementCatalogueLanguage.NormalizeIdentification(value) == identity)).ToArray();
                            if (countables.Length == 1) { arguments = arguments with { Quantity = count, CountableOutputValue = countables[0].OutputValue }; operands.Add(new("countable", countables[0].Id, countables[0].OutputValue, countableText)); }
                            else failure = countables.Length == 0 ? $"unrecognized countable: {countableText}" : $"ambiguous countable: {countableText}";
                        }
                    }
                    break;
                case RequirementInputSyntax.PredicateThenCountableThenQuantity:
                    var countableFirstPrefix = PrefixLength(raw, alias);
                    if (countableFirstPrefix >= 0)
                    {
                        matched = true;
                        if (!RequirementTypedQuantity.TryReadLast(raw[countableFirstPrefix..], out var finalCount, out var countableFirstText))
                            failure = $"a countable and final canonical positive Int32 quantity are required for: {raw}";
                        else
                        {
                            var countableFirstIdentity = RequirementCatalogueLanguage.NormalizeIdentification(countableFirstText);
                            var countableFirstMatches = context.Countables.Where(countable => countable.Aliases.Any(value => RequirementCatalogueLanguage.NormalizeIdentification(value) == countableFirstIdentity)).ToArray();
                            if (countableFirstMatches.Length == 1) { arguments = arguments with { Quantity = finalCount, CountableOutputValue = countableFirstMatches[0].OutputValue }; operands.Add(new("countable", countableFirstMatches[0].Id, countableFirstMatches[0].OutputValue, countableFirstText)); }
                            else failure = countableFirstMatches.Length == 0 ? $"unrecognized countable: {countableFirstText}" : $"ambiguous countable: {countableFirstText}";
                        }
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            if (!matched) return;
            if (failure is not null) { candidates.Add(new(null, failure)); return; }
            try { candidates.Add(new(MakeAtom(predicate, arguments, operands, dependencyId), null)); }
            catch (Exception exception) { candidates.Add(new(null, $"predicate '{predicate.Name}' could not format its output: {exception.Message}")); }
        }

        private void AddOperandCandidates(RoomGraphPredicateDefinition predicate, string alias, string raw, List<AtomCandidate> candidates)
        {
            var slots = RequirementOperandSyntax.Describe(predicate.Syntax);
            // Keep distinct managed aliases/definitions and typed identities even when outputs coincide.
            var interpretations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parts in RequirementOperandSyntax.Partitions(raw, slots))
            {
                var predicateIndex = slots.ToList().FindIndex(slot => slot.Role == "predicate");
                if (parts[predicateIndex].IndexOfAny(['[', ']', ',']) >= 0 ||
                    RequirementCatalogueLanguage.NormalizeIdentification(parts[predicateIndex]) != RequirementCatalogueLanguage.NormalizeIdentification(alias)) continue;
                IReadOnlyList<(RequirementAtomFormatArguments Arguments, IReadOnlyList<AuditOperand> Operands)> combinations = [(new(), [])];
                var identities = new List<string>();
                string? failure = null;
                for (var index = 0; index < slots.Count; index++)
                {
                    var slot = slots[index];
                    if (slot.Role == "predicate") continue;
                    if (slot.Role == "quantity")
                    {
                        if (!RequirementOperandSyntax.TryQuantity(parts[index], slot.Optional, out var quantity))
                        { failure = $"invalid or missing canonical positive Int32 quantity: {parts[index]}"; break; }
                        identities.Add(parts[index].Trim());
                        combinations = combinations.Select(combination => (combination.Arguments with { Quantity = quantity }, combination.Operands)).ToArray();
                        continue;
                    }
                    if (!RequirementOperandSyntax.TryValues(parts[index], slot, out var values))
                    { failure = $"invalid or missing {slot.Role} operand/list: {parts[index]}"; break; }
                    identities.Add(string.Join('\u001f', values.Select(RequirementCatalogueLanguage.NormalizeIdentification)));
                    var alternatives = new List<AuditOperand>();
                    foreach (var value in values)
                    {
                        var identity = RequirementCatalogueLanguage.NormalizeIdentification(value);
                        if (slot.Role == "crest" && slot.Optional && parts[index].Length == 0)
                            alternatives.Add(new(slot.Role, null, "none", ""));
                        else if (slot.Role == "difficulty" || slot.Role == "direction")
                        {
                            if (slot.Role == "direction" && slot.Optional && parts[index].Length == 0) alternatives.Add(new(slot.Role, null, "none", ""));
                            else if ((slot.Role == "difficulty" ? Difficulties : Directions).Contains(identity)) alternatives.Add(new(slot.Role, null, identity, value));
                            else { failure = $"invalid {slot.Role}: {value}"; break; }
                        }
                        else
                        {
                            var catalogue = slot.Role switch
                            {
                                "crest" => context.Crests,
                                "method" => context.Methods,
                                "soul" => context.Souls,
                                _ => throw new InvalidOperationException("Unexpected approved typed operand role.")
                            };
                            var matches = catalogue.Where(row => row.Aliases.Any(candidate => RequirementCatalogueLanguage.NormalizeIdentification(candidate) == identity)).ToArray();
                            if (matches.Length != 1) { failure = $"{(matches.Length == 0 ? "unrecognized" : "ambiguous")} {slot.Role}: {value}"; break; }
                            alternatives.Add(new(slot.Role, matches[0].Id, matches[0].OutputValue, value));
                        }
                    }
                    if (failure is not null) break;
                    // Ordered product: each existing prefix combines with every next alternative.
                    // Validate the whole application before formatting any resulting atom.
                    combinations = combinations.SelectMany(combination => alternatives.Select(operand => (slot.Role switch
                    {
                        "difficulty" => combination.Arguments with { Difficulty = operand.Value },
                        "direction" => combination.Arguments with { Direction = operand.Value },
                        "crest" => combination.Arguments with { CrestOutputValue = operand.Value },
                        "method" => combination.Arguments with { MethodOutputValue = operand.Value },
                        "soul" => combination.Arguments with { SoulOutputValue = operand.Value },
                        _ => throw new InvalidOperationException("Unexpected approved operand role.")
                    }, (IReadOnlyList<AuditOperand>)[.. combination.Operands, operand]))).ToArray();
                }
                if (failure is not null) { candidates.Add(new(null, failure)); continue; }
                // Only equivalent complete raw allocations collapse. Entry order/multiplicity remain in the key
                // and in the product; no output atom, list entry or DNF clause is deduplicated.
                if (!interpretations.Add(string.Join('\u001e', identities))) continue;
                try
                {
                    var atoms = combinations.Select(combination => MakeAtom(predicate, combination.Arguments, combination.Operands, null)).ToArray();
                    var grouped = atoms.Aggregate((left, right) => new OrNode(left, right));
                    candidates.Add(new(grouped, null));
                }
                catch (Exception exception) { candidates.Add(new(null, $"predicate '{predicate.Name}' could not format its output: {exception.Message}")); }
            }
        }

        private Node MakeAtom(RoomGraphPredicateDefinition predicate, RequirementAtomFormatArguments arguments, IReadOnlyList<AuditOperand> operands, Guid? dependencyId) =>
            new AtomNode(RequirementAtomFormatter.Format(predicate.Syntax, predicate.OutputSyntax, arguments),
                new(predicate.Id, predicate.OutputSyntax, arguments, operands, dependencyId, atomStart, atomLength));

        private (string? LocationId, string? Failure, Guid? CheckId) ResolveDependency(string value)
        {
            var operand = value.TrimStart();
            if (operand.Length == 0) return (null, "required check name is missing", null);
            var qualifiers = FindQualifiers(operand);
            if (qualifiers.Any(value => value.Word == "GLOBAL")) return (null, "GLOBAL is not supported; use THE for full-catalogue scope", null);
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
                if (checkName.Length == 0 || roomText.Length == 0) return (null, "check dependency has an incomplete IN scope", null);
                var roomIdentity = RequirementCatalogueLanguage.NormalizeIdentification(roomText);
                var rooms = context.Rooms.Where(room => RequirementCatalogueLanguage.NormalizeIdentification(room.ReferenceId) == roomIdentity).ToArray();
                if (rooms.Length != 1) return (null, rooms.Length == 0 ? $"dependency room was not found: {roomText}" : $"dependency room is ambiguous: {roomText}", null);
                targetRoom = rooms[0].Id;
            }
            else return (null, "check dependency has invalid or conflicting scope syntax", null);

            if (string.IsNullOrWhiteSpace(checkName)) return (null, "required check name is missing", null);
            var identity = RequirementCatalogueLanguage.NormalizeIdentification(checkName);
            var checks = context.Checks.Where(check => (targetRoom is null || check.RoomId == targetRoom) &&
                RequirementCatalogueLanguage.NormalizeIdentification(check.FriendlyName) == identity).ToArray();
            if (checks.Length != 1) return (null, checks.Length == 0 ? $"dependency check was not found: {checkName}" : $"dependency check is ambiguous: {checkName}", null);
            return checks[0].LocationId is { } id ? (id, null, checks[0].Id) : (null, $"dependency check has no valid export location identity: {checkName}", null);
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
