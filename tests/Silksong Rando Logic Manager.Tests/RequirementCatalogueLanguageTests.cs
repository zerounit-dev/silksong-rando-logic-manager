using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class RequirementCatalogueLanguageTests
{
    [Fact]
    public void InputSyntaxes_HaveExactPersistedValuesAndDisplayLabels()
    {
        var expected = new[]
        {
            "{predicate}", "{difficulty?} {predicate}", "{difficulty} {predicate}",
            "{predicate} {direction}", "{predicate} {item}", "{predicate} {check}", "{predicate} {quantity}"
        };

        Assert.Equal(expected, RequirementCatalogueLanguage.InputSyntaxOptions.Select(option => option.PersistedValue));
        Assert.All(RequirementCatalogueLanguage.InputSyntaxOptions, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.DisplayLabel));
            Assert.True(RequirementCatalogueLanguage.TryParsePersistedValue(option.PersistedValue, out var parsed));
            Assert.Equal(option.Value, parsed);
        });
        Assert.False(RequirementCatalogueLanguage.TryParsePersistedValue("{predicate} {unknown}", out _));
    }

    [Fact]
    public void ParseAliases_SplitsTrimsCollapsesAndPreservesAuthoredSpellingAndOrder()
    {
        var aliases = RequirementCatalogueLanguage.ParseAliases("  Have,  silk\tsoar  , , O'Rún-2  ");

        Assert.Equal(new[] { "Have", "silk soar", "O'Rún-2" }, aliases);
    }

    [Fact]
    public void NormalizeIdentification_UsesUnicodeLettersDecimalDigitsWhitespaceAndInvariantCase()
    {
        Assert.Equal("rún٢ alpha2", RequirementCatalogueLanguage.NormalizeIdentification("  RÚN-٢!!\tALPHA2  "));
        Assert.Equal("have needle2", RequirementCatalogueLanguage.NormalizeIdentification("Have Needle-2"));
        Assert.Equal(string.Empty, RequirementCatalogueLanguage.NormalizeIdentification("---"));
    }

    [Fact]
    public void Validate_RejectsSameNamespaceAndSameParentAliasCollisions_ButAllowsTypedOverlapAndPrefixes()
    {
        var firstPredicateId = Guid.NewGuid();
        var secondPredicateId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var issues = RequirementCatalogueValidator.Validate(
            new[]
            {
                Predicate(firstPredicateId, "First", "have", "have needle"),
                Predicate(secondPredicateId, "Second", "HAVE"),
                Predicate(Guid.NewGuid(), "Prefix", "have needle 2")
            },
            new[] { Item(itemId, "Item", "have") });

        var collision = Assert.Single(issues);
        Assert.Equal("Aliases", collision.Field);
        Assert.Equal(secondPredicateId, collision.DefinitionId);
        Assert.Equal(firstPredicateId, collision.CollidingDefinitionId);
        Assert.Equal("First", collision.CollidingDefinitionName);
    }

    [Fact]
    public void Validate_RequiresDefinitionFieldsAndAliases()
    {
        var predicateId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var issues = RequirementCatalogueValidator.Validate(
            new[] { new RequirementCataloguePredicateValidation(predicateId, " ", null, (RequirementInputSyntax)99, "", null, "") },
            new[] { new RequirementCatalogueItemValidation(itemId, null, null, " ", null, null) });

        Assert.Contains(issues, issue => issue.DefinitionId == predicateId && issue.Field == "Name");
        Assert.Contains(issues, issue => issue.DefinitionId == predicateId && issue.Field == "Notes");
        Assert.Contains(issues, issue => issue.DefinitionId == predicateId && issue.Field == "Aliases");
        Assert.Contains(issues, issue => issue.DefinitionId == predicateId && issue.Field == "InputSyntax");
        Assert.Contains(issues, issue => issue.DefinitionId == itemId && issue.Field == "OutputValue");
        Assert.Contains(issues, issue => issue.DefinitionId == itemId && issue.Field == "Aliases");
    }

    [Theory]
    [InlineData(RequirementInputSyntax.PredicateOnly, "atom")]
    [InlineData(RequirementInputSyntax.OptionalDifficultyThenPredicate, "atom:{difficulty}")]
    [InlineData(RequirementInputSyntax.RequiredDifficultyThenPredicate, "atom:{difficulty}")]
    [InlineData(RequirementInputSyntax.PredicateThenDirection, "atom:{direction}")]
    [InlineData(RequirementInputSyntax.PredicateThenItem, "atom:{item}")]
    [InlineData(RequirementInputSyntax.PredicateThenCheck, "atom:{check}")]
    [InlineData(RequirementInputSyntax.PredicateThenQuantity, "atom:{quantity}")]
    public void Validate_AcceptsEachSupportedOutputShape(RequirementInputSyntax inputSyntax, string outputSyntax)
    {
        var issues = RequirementCatalogueValidator.Validate(new[] { Predicate(Guid.NewGuid(), "Predicate", new[] { "alias" }, inputSyntax, outputSyntax) }, Array.Empty<RequirementCatalogueItemValidation>());

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("atom:{predicate}")]
    [InlineData("atom:{unknown}")]
    [InlineData("atom:{difficulty")]
    [InlineData("atom:difficulty}")]
    [InlineData("atom:{difficulty}{difficulty}")]
    [InlineData("atom")]
    public void Validate_RejectsMalformedUnknownDuplicateOrMissingOutputArguments(string outputSyntax)
    {
        var issues = RequirementCatalogueValidator.Validate(
            new[] { Predicate(Guid.NewGuid(), "Predicate", new[] { "alias" }, RequirementInputSyntax.PredicateThenDirection, outputSyntax) },
            Array.Empty<RequirementCatalogueItemValidation>());

        Assert.NotEmpty(issues);
        Assert.All(issues, issue => Assert.Equal("OutputSyntax", issue.Field));
    }

    private static RequirementCataloguePredicateValidation Predicate(Guid id, string name, params string[] aliases) =>
        Predicate(id, name, aliases, RequirementInputSyntax.PredicateOnly, "atom");

    private static RequirementCataloguePredicateValidation Predicate(Guid id, string name, IEnumerable<string> aliases, RequirementInputSyntax inputSyntax, string outputSyntax) =>
        new(id, name, null, inputSyntax, outputSyntax, string.Empty, string.Join(", ", aliases));

    private static RequirementCatalogueItemValidation Item(Guid id, string name, params string[] aliases) =>
        new(id, name, null, "item", string.Empty, string.Join(", ", aliases));
}
