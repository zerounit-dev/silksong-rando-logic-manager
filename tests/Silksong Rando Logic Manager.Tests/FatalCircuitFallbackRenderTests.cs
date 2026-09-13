using Bunit;
using Silksong_Rando_Logic_Manager.Components;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class FatalCircuitFallbackRenderTests
{
    [Fact]
    public void Fallback_RendersStaticCircuitTerminationDetailsAndRecoveryActions()
    {
        using var context = new TestContext();
        var component = context.Render<FatalCircuitFallback>(builder =>
        {
            builder.OpenComponent<FatalCircuitFallback>(0);
            builder.CloseComponent();
        });

        var fallback = component.Find("#blazor-error-ui");
        Assert.Equal("true", fallback.GetAttribute("data-fatal-circuit-fallback"));
        Assert.Equal("true", fallback.GetAttribute("aria-hidden"));
        Assert.Contains("Circuit terminated", component.Markup);
        Assert.Contains("Browser-available details", component.Markup);
        Assert.Contains("id=\"fatal-circuit-details\"", component.Markup);
        Assert.Contains("readonly", component.Markup);
        Assert.Contains("id=\"fatal-circuit-copy\"", component.Markup);
        Assert.Contains("id=\"fatal-circuit-reload\"", component.Markup);
        Assert.DoesNotContain("server stack trace", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("close", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retry", component.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
