using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

public static class RequirementCatalogueLanguage
{
    private static readonly IReadOnlyList<RequirementInputSyntaxOption> inputSyntaxOptions = new[]
    {
        new RequirementInputSyntaxOption(RequirementInputSyntax.PredicateOnly, "{predicate}", "Predicate only"),
        new RequirementInputSyntaxOption(RequirementInputSyntax.OptionalDifficultyThenPredicate, "{difficulty?} {predicate}", "Optional difficulty + predicate"),
        new RequirementInputSyntaxOption(RequirementInputSyntax.RequiredDifficultyThenPredicate, "{difficulty} {predicate}", "Required difficulty + predicate"),
        new RequirementInputSyntaxOption(RequirementInputSyntax.PredicateThenDirection, "{predicate} {direction}", "Predicate + direction"),
        new RequirementInputSyntaxOption(RequirementInputSyntax.PredicateThenItem, "{predicate} {item}", "Predicate + item"),
        new RequirementInputSyntaxOption(RequirementInputSyntax.PredicateThenCheck, "{predicate} {check}", "Predicate + check"),
        new RequirementInputSyntaxOption(RequirementInputSyntax.PredicateThenQuantity, "{predicate} {quantity}", "Predicate + quantity")
    };

    public static IReadOnlyList<RequirementInputSyntaxOption> InputSyntaxOptions => inputSyntaxOptions;

    public static string ToPersistedValue(RequirementInputSyntax inputSyntax) => FindOption(inputSyntax).PersistedValue;

    public static string GetDisplayLabel(RequirementInputSyntax inputSyntax) => FindOption(inputSyntax).DisplayLabel;

    public static bool TryParsePersistedValue(string? value, out RequirementInputSyntax inputSyntax)
    {
        foreach (var option in inputSyntaxOptions)
        {
            if (string.Equals(option.PersistedValue, value, StringComparison.Ordinal))
            {
                inputSyntax = option.Value;
                return true;
            }
        }

        inputSyntax = default;
        return false;
    }

    public static IReadOnlyList<string> ParseAliases(string? aliasDraft)
    {
        if (string.IsNullOrEmpty(aliasDraft)) return Array.Empty<string>();

        return aliasDraft.Split(',')
            .Select(CollapseAuthoredWhitespace)
            .Where(alias => alias.Length != 0)
            .ToArray();
    }

    public static bool TryCanonicalizeAliases(string? aliasDraft, out string canonicalAliases, out IReadOnlyList<string> aliases, out string? error)
    {
        var parsed = ParseAliases(aliasDraft);
        if (parsed.Count == 0)
        {
            canonicalAliases = string.Empty; aliases = parsed; error = "At least one alias is required."; return false;
        }

        foreach (var alias in parsed)
        {
            if (alias.EnumerateRunes().Any(rune => !Rune.IsLetter(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber && !Rune.IsWhiteSpace(rune)))
            {
                canonicalAliases = string.Empty; aliases = parsed; error = "Aliases may contain only Unicode letters, decimal digits, and whitespace."; return false;
            }
        }

        var identities = new HashSet<string>();
        if (parsed.Any(alias => !identities.Add(NormalizeIdentification(alias))))
        {
            canonicalAliases = string.Empty; aliases = parsed; error = "An alias is repeated in this definition."; return false;
        }

        aliases = parsed; canonicalAliases = string.Join(", ", parsed); error = null; return true;
    }

    public static string CollapseAuthoredWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length != 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(rune);
        }

        return result.ToString();
    }

    public static string NormalizeIdentification(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length != 0;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetter(rune) || category == UnicodeCategory.DecimalDigitNumber)
            {
                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }

                result.Append(Rune.ToLowerInvariant(rune));
            }
        }

        return result.ToString();
    }

    public static IReadOnlySet<RequirementOutputPlaceholder> GetSupportedOutputArguments(RequirementInputSyntax inputSyntax) => inputSyntax switch
    {
        RequirementInputSyntax.PredicateOnly => new HashSet<RequirementOutputPlaceholder>(),
        RequirementInputSyntax.OptionalDifficultyThenPredicate or RequirementInputSyntax.RequiredDifficultyThenPredicate => new HashSet<RequirementOutputPlaceholder> { RequirementOutputPlaceholder.Difficulty },
        RequirementInputSyntax.PredicateThenDirection => new HashSet<RequirementOutputPlaceholder> { RequirementOutputPlaceholder.Direction },
        RequirementInputSyntax.PredicateThenItem => new HashSet<RequirementOutputPlaceholder> { RequirementOutputPlaceholder.Item },
        RequirementInputSyntax.PredicateThenCheck => new HashSet<RequirementOutputPlaceholder> { RequirementOutputPlaceholder.Check },
        RequirementInputSyntax.PredicateThenQuantity => new HashSet<RequirementOutputPlaceholder> { RequirementOutputPlaceholder.Quantity },
        _ => throw new ArgumentOutOfRangeException(nameof(inputSyntax), inputSyntax, "Unsupported requirement input syntax.")
    };

    private static RequirementInputSyntaxOption FindOption(RequirementInputSyntax inputSyntax) =>
        inputSyntaxOptions.FirstOrDefault(option => option.Value == inputSyntax)
        ?? throw new ArgumentOutOfRangeException(nameof(inputSyntax), inputSyntax, "Unsupported requirement input syntax.");
}

public enum RequirementOutputPlaceholder
{
    Difficulty,
    Direction,
    Item,
    Quantity,
    Check
}

public abstract record RequirementOutputToken;
public sealed record RequirementOutputLiteralToken(string Value) : RequirementOutputToken;
public sealed record RequirementOutputPlaceholderToken(RequirementOutputPlaceholder Value) : RequirementOutputToken;

public static class RequirementOutputTemplate
{
    public static bool TryTokenize(string? template, out IReadOnlyList<RequirementOutputToken> tokens, out string? error)
    {
        var result = new List<RequirementOutputToken>();
        if (template is null)
        {
            tokens = result;
            error = "Output syntax is required.";
            return false;
        }

        var literal = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character == '}')
            {
                tokens = result;
                error = "Output syntax has an unmatched closing brace.";
                return false;
            }

            if (character != '{')
            {
                literal.Append(character);
                continue;
            }

            if (literal.Length != 0)
            {
                result.Add(new RequirementOutputLiteralToken(literal.ToString()));
                literal.Clear();
            }

            var closingBrace = template.IndexOf('}', index + 1);
            if (closingBrace < 0)
            {
                tokens = result;
                error = "Output syntax has an unmatched opening brace.";
                return false;
            }

            var placeholderText = template[(index + 1)..closingBrace];
            if (placeholderText.Contains('{'))
            {
                tokens = result;
                error = "Output syntax has malformed braces.";
                return false;
            }

            if (!TryParsePlaceholder(placeholderText, out var placeholder))
            {
                tokens = result;
                error = $"Output syntax placeholder '{{{placeholderText}}}' is not supported.";
                return false;
            }

            result.Add(new RequirementOutputPlaceholderToken(placeholder));
            index = closingBrace;
        }

        if (literal.Length != 0) result.Add(new RequirementOutputLiteralToken(literal.ToString()));
        tokens = result;
        error = null;
        return true;
    }

    private static bool TryParsePlaceholder(string value, out RequirementOutputPlaceholder placeholder)
    {
        placeholder = value switch
        {
            "difficulty" => RequirementOutputPlaceholder.Difficulty,
            "direction" => RequirementOutputPlaceholder.Direction,
            "item" => RequirementOutputPlaceholder.Item,
            "quantity" => RequirementOutputPlaceholder.Quantity,
            "check" => RequirementOutputPlaceholder.Check,
            _ => default
        };
        return value is "difficulty" or "direction" or "item" or "quantity" or "check";
    }
}
