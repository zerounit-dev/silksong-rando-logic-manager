using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class RequirementAtomFormatterTests
{
    [Fact]
    public void Format_UsesNoneForAbsentOptionalDifficulty()
    {
        var output = RequirementAtomFormatter.Format(RequirementInputSyntax.OptionalDifficultyThenPredicate, "technique:{difficulty}", new RequirementAtomFormatArguments());

        Assert.Equal("technique:none", output);
    }

    [Fact]
    public void Format_SubstitutesOptionalDifficultyWhenPresent()
    {
        var output = RequirementAtomFormatter.Format(RequirementInputSyntax.OptionalDifficultyThenPredicate, "technique:{difficulty}", new RequirementAtomFormatArguments(Difficulty: "hard"));

        Assert.Equal("technique:hard", output);
    }

    [Fact]
    public void Format_SubstitutesItemDirectionQuantityAndCheckVerbatim()
    {
        Assert.Equal("have:curveclaw", RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateThenItem, "have:{item}", new RequirementAtomFormatArguments(ItemOutputValue: "curveclaw")));
        Assert.Equal("break-vines:left", RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateThenDirection, "break-vines:{direction}", new RequirementAtomFormatArguments(Direction: "left")));
        Assert.Equal("count:rosary:12", RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateThenQuantity, "count:rosary:{quantity}", new RequirementAtomFormatArguments(Quantity: 12)));
        Assert.Equal("location:area:room:check", RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateThenCheck, "location:{check}", new RequirementAtomFormatArguments(CheckGraphId: "area:room:check")));
    }

    [Fact]
    public void Format_RequiresSupportedArgumentsExactlyOnceAndPositiveQuantity()
    {
        Assert.Throws<ArgumentException>(() => RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateThenDirection, "atom", new RequirementAtomFormatArguments()));
        Assert.Throws<ArgumentException>(() => RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateOnly, "atom:{direction}", new RequirementAtomFormatArguments(Direction: "left")));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateThenQuantity, "atom:{quantity}", new RequirementAtomFormatArguments(Quantity: 0)));
    }

    [Fact]
    public void Format_EmitsOneNonblankAtomString()
    {
        Assert.Equal("impassable", RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateOnly, "impassable", new RequirementAtomFormatArguments()));
        Assert.Throws<ArgumentException>(() => RequirementAtomFormatter.Format(RequirementInputSyntax.PredicateOnly, "  ", new RequirementAtomFormatArguments()));
    }
}
