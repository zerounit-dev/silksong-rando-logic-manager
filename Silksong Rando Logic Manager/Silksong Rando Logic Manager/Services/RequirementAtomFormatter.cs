using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

public static class RequirementAtomFormatter
{
    public static string Format(RequirementInputSyntax inputSyntax, string outputSyntax, RequirementAtomFormatArguments arguments)
    {
        if (!RequirementOutputTemplate.TryTokenize(outputSyntax, out var tokens, out var error))
        {
            throw new ArgumentException(error, nameof(outputSyntax));
        }

        if (RequirementOutputTemplate.HasBracketedOns1Placeholder(inputSyntax, outputSyntax))
            throw new ArgumentException("Output placeholders must not use input list brackets.", nameof(outputSyntax));

        var required = RequirementCatalogueLanguage.GetSupportedOutputArguments(inputSyntax);
        var placeholders = tokens.OfType<RequirementOutputPlaceholderToken>().Select(token => token.Value).ToArray();
        if (placeholders.Distinct().Count() != placeholders.Length || placeholders.Any(placeholder => !required.Contains(placeholder)) || required.Any(placeholder => !placeholders.Contains(placeholder)))
        {
            throw new ArgumentException("Output syntax is not valid for the input syntax.", nameof(outputSyntax));
        }

        var output = new StringBuilder();
        foreach (var token in tokens)
        {
            output.Append(token switch
            {
                RequirementOutputLiteralToken literal => literal.Value,
                RequirementOutputPlaceholderToken placeholder => GetValue(inputSyntax, placeholder.Value, arguments),
                _ => throw new InvalidOperationException("Unknown output template token.")
            });
        }

        if (string.IsNullOrWhiteSpace(output.ToString())) throw new ArgumentException("Output syntax must emit a nonblank atom.", nameof(outputSyntax));
        return output.ToString();
    }

    private static string GetValue(RequirementInputSyntax inputSyntax, RequirementOutputPlaceholder placeholder, RequirementAtomFormatArguments arguments) => placeholder switch
    {
        RequirementOutputPlaceholder.Difficulty => inputSyntax == RequirementInputSyntax.OptionalDifficultyThenPredicate
            ? string.IsNullOrWhiteSpace(arguments.Difficulty) ? "none" : arguments.Difficulty
            : Required(arguments.Difficulty, "difficulty"),
        RequirementOutputPlaceholder.Direction => inputSyntax == RequirementInputSyntax.PredicateThenOptionalDirection
            ? string.IsNullOrWhiteSpace(arguments.Direction) ? "none" : arguments.Direction
            : Required(arguments.Direction, "direction"),
        RequirementOutputPlaceholder.Item => Required(arguments.ItemOutputValue, "item output value"),
        RequirementOutputPlaceholder.Quantity => PositiveQuantity(arguments.Quantity),
        RequirementOutputPlaceholder.Check => Required(arguments.CheckGraphId, "check graph ID"),
        RequirementOutputPlaceholder.Soul => Required(arguments.SoulOutputValue, "soul output value"),
        RequirementOutputPlaceholder.Countable => Required(arguments.CountableOutputValue, "countable output value"),
        RequirementOutputPlaceholder.Crest => inputSyntax is RequirementInputSyntax.OptionalCrestThenPredicateThenDirection or RequirementInputSyntax.RequiredDifficultyThenOptionalCrestThenPredicate
            ? string.IsNullOrWhiteSpace(arguments.CrestOutputValue) ? "none" : arguments.CrestOutputValue
            : Required(arguments.CrestOutputValue, "crest output value"),
        _ => throw new ArgumentOutOfRangeException(nameof(placeholder))
    };

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"A nonblank {name} is required.", name);

    private static string PositiveQuantity(int? quantity) => quantity is > 0
        ? quantity.Value.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be a positive Int32.");
}
