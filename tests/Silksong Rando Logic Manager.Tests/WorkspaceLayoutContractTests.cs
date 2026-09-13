using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class WorkspaceLayoutContractTests
{
    [Fact]
    public void SidebarSceneTrigger_ActivatesTheStableNativePickerExactlyOnce()
    {
        using var context = new TestContext();
        context.JSInterop.SetupVoid("openSceneUploadPicker", invocation =>
            invocation.Arguments.Count == 1 && (string?)invocation.Arguments[0] == "scene-upload-input");

        var trigger = context.RenderComponent<SceneReviewTrigger>();
        trigger.Find("button.scene-review-trigger").Click();

        Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "openSceneUploadPicker");
    }

    [Fact]
    public void SharedShell_UsesTheSettledSidebarWorkspaceAndContainerContract()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));
        var layout = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("html, body { min-width: 1050px; height: 100%; margin: 0; overflow-y: hidden;", css);
        Assert.Contains(".app-shell { min-width: 1050px; height: 100vh; display: grid; grid-template-columns: 198px 1.35rem minmax(0, 1fr); overflow: hidden; }", css);
        Assert.DoesNotContain("column-gap:", css);
        Assert.DoesNotContain("grid-template-columns: 198px 1.35rem minmax(0, 1fr) 1.35rem;", css);
        Assert.Contains(".sidebar { grid-column: 1;", css);
        Assert.Contains(".workspace { grid-column: 3; min-width: 0; height: 100vh; overflow-y: auto; padding: 0 1.35rem 0 0; }", css);
        Assert.Contains(".content { width: min(100%, 1050px); margin: 0; padding: 1rem 0; }", css);
        Assert.Contains(".content:has(> .room-editor-v2), .content:has(> .room-document) { padding: 0; }", css);
        Assert.DoesNotContain("@media (max-width: 1050px)", css);
        Assert.Contains("<aside class=\"sidebar\"", layout);
        Assert.Contains("<main class=\"workspace\">", layout);
        Assert.Contains("<article class=\"content\">", layout);
        Assert.Contains("<SceneReview />", layout);
        Assert.DoesNotContain("@ref=\"sceneReview\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("SceneReviewTrigger", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("@rendermode", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedShell_AllowsOrdinaryWorkspaceScrollingOnlyWhenNeededWithoutRemovingFixedCanvasReachability()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));

        Assert.Contains("html, body { min-width: 1050px; height: 100%; margin: 0; overflow-y: hidden;", css);
        Assert.Contains(".app-shell { min-width: 1050px; height: 100vh;", css);
        Assert.Contains(".workspace { grid-column: 3; min-width: 0; height: 100vh; overflow-y: auto; padding: 0 1.35rem 0 0; }", css);
        Assert.DoesNotContain(".workspace { grid-column: 3; min-width: 0; height: 100vh; overflow-y: scroll;", css);
        Assert.DoesNotContain(".workspace { grid-column: 3; min-width: 0; height: 100vh; overflow-y: auto; scrollbar-gutter:", css);
        Assert.DoesNotContain("html, body { min-width: 1050px; height: 100%; margin: 0; overflow-x: hidden;", css);
        Assert.DoesNotContain(".app-shell { min-width: 1050px; height: 100vh; display: grid; grid-template-columns: 198px 1.35rem minmax(0, 1fr) 1.35rem; column-gap:", css);
    }

    [Fact]
    public void SidebarAndRoomDocuments_UseNonStickyViewportGridBands()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));

        Assert.Contains(".sidebar { grid-column: 1; height: 100vh; overflow: hidden;", css);
        Assert.Contains(".sidebar-layout { display: grid; grid-template-rows: auto minmax(0, 1fr) auto; height: 100%; }", css);
        Assert.Contains(".room-document { display: grid; grid-template-rows: auto minmax(0, 1fr) auto; width: 100%; height: 100vh; min-height: 0; overflow: hidden; }", css);
        Assert.Contains(".room-document-content { width: 100%; min-height: 0; overflow-y: scroll; scrollbar-gutter: stable;", css);
        Assert.Contains(".room-sticky-context { width: 100%; margin: 0; padding: 1rem 0 .4rem;", css);
        Assert.Contains(".room-editor-v2 .room-sticky-context { width: 100%; margin: 0; padding: 1rem 0 0;", css);
        Assert.DoesNotMatch(@"\.sidebar\s*\{[^}]*\b(position\s*:\s*sticky|z-index\s*:)", css);
        Assert.DoesNotMatch(@"\.room-sticky-context\s*\{[^}]*\b(position\s*:\s*sticky|z-index\s*:|isolation\s*:)", css);
        Assert.DoesNotMatch(@"\.room-editor-v2 \.room-sticky-context\s*\{[^}]*\b(position\s*:\s*sticky|z-index\s*:|isolation\s*:)", css);
        Assert.DoesNotMatch(@"\.room-editor-v2 \.room-reminder-footer\s*\{[^}]*\b(position\s*:\s*sticky|z-index\s*:|isolation\s*:)", css);
    }

    [Fact]
    public void ModalHosts_EscapeSidebarAndRoomBandsAndRetainEffectiveLayerCascade()
    {
        var root = FindRepositoryRoot();
        var nav = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Layout", "NavMenu.razor"));
        var layout = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Layout", "MainLayout.razor"));
        var app = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "App.razor"));
        var review = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "SceneReview.razor"));
        var trigger = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "SceneReviewTrigger.razor"));
        var editor = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "editor.js"));
        var diagnosticCss = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "DiagnosticModal.razor.css"));
        var v2ModalHost = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "V2PageModalHost.razor"));
        var roomMap = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "AreaMapPanePresentation.razor"));
        var landingMap = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Pages", "MapLanding.razor"));
        var mapLinks = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "MapLinkEditor.razor"));
        var reconnect = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Layout", "ReconnectModal.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));

        var footerStart = nav.IndexOf("<footer class=\"sidebar-export-footer\">", StringComparison.Ordinal);
        var footerEnd = nav.IndexOf("</footer>", footerStart, StringComparison.Ordinal);
        var footer = nav.Substring(footerStart, footerEnd - footerStart);
        Assert.DoesNotContain("<SceneReview />", nav, StringComparison.Ordinal);
        Assert.Contains("<SceneReviewTrigger />", footer);
        Assert.True(footer.IndexOf("<SceneReviewTrigger />", StringComparison.Ordinal) < footer.IndexOf("sidebar-export-row", StringComparison.Ordinal));
        Assert.Contains("<InputFile id=\"scene-upload-input\"", review);
        Assert.Contains("registerSceneDropTarget", review);
        Assert.Contains("private bool UploadUnavailable => isParsing || review is not null;", review);
        Assert.Contains("OnChange=\"ParseSceneAsync\"", review);
        Assert.DoesNotContain("OpenPickerAsync", review, StringComparison.Ordinal);
        Assert.DoesNotContain("CascadingParameter", trigger, StringComparison.Ordinal);
        Assert.Contains("IJSRuntime JS", trigger);
        Assert.Contains("JS.InvokeVoidAsync(\"openSceneUploadPicker\", \"scene-upload-input\")", trigger);
        Assert.Contains("window.openSceneUploadPicker", editor);
        Assert.Contains(".sidebar-export-footer .scene-review-trigger { display: flex; width: 100%; justify-content: center; }", css);

        Assert.Contains("<aside class=\"sidebar\"", layout);
        Assert.Contains("<NavMenu />", layout);
        Assert.True(layout.IndexOf("</div>\n        <SceneReview />", StringComparison.Ordinal) >= 0);
        Assert.Contains("<div class=\"scene-parsing-backdrop\"", review);
        Assert.Contains("<div class=\"scene-review-backdrop\">", review);
        Assert.Contains("class=\"confirmation-backdrop v2-page-modal-backdrop\"", v2ModalHost);
        Assert.Contains("<MapLinkEditor", roomMap);
        Assert.Contains("<MapLinkEditor", landingMap);
        Assert.Contains("<div class=\"confirmation-backdrop\">", mapLinks);
        Assert.Contains("<FatalCircuitFallback />", app);
        Assert.Contains("<dialog id=\"components-reconnect-modal\"", reconnect);

        var sceneReviewLayer = ZIndex(css, ".scene-parsing-backdrop, .scene-review-backdrop");
        var genericBackdropLayer = EffectiveZIndex(css, "confirmation-backdrop");
        var v2BackdropLayer = EffectiveZIndex(css, "confirmation-backdrop", "v2-page-modal-backdrop");
        var diagnosticLayer = ZIndex(diagnosticCss, ".diagnostic-backdrop");
        var fatalLayer = ZIndex(css, ".fatal-circuit-fallback");

        Assert.DoesNotMatch(@"\.sidebar\s*\{[^}]*\bz-index\s*:", css);
        Assert.Equal(1500, sceneReviewLayer);
        Assert.Equal(1000, genericBackdropLayer);
        Assert.Equal(2000, v2BackdropLayer);
        Assert.True(v2BackdropLayer > genericBackdropLayer);
        Assert.True(diagnosticLayer > v2BackdropLayer);
        Assert.True(sceneReviewLayer > genericBackdropLayer);
        Assert.True(sceneReviewLayer < v2BackdropLayer);
        Assert.Equal(3000, fatalLayer);
        Assert.True(fatalLayer > diagnosticLayer);
    }

    private static int ZIndex(string css, string selector)
    {
        var ruleStart = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(ruleStart >= 0, $"Missing CSS rule for {selector}.");
        var ruleEnd = css.IndexOf('}', ruleStart);
        Assert.True(ruleEnd > ruleStart, $"Unterminated CSS rule for {selector}.");
        var declaration = css.Substring(ruleStart, ruleEnd - ruleStart);
        var zIndexStart = declaration.IndexOf("z-index: ", StringComparison.Ordinal);
        Assert.True(zIndexStart >= 0, $"Missing z-index for {selector}.");
        var valueStart = zIndexStart + "z-index: ".Length;
        var valueEnd = declaration.IndexOf(';', valueStart);
        Assert.True(valueEnd > valueStart, $"Invalid z-index for {selector}.");
        return int.Parse(declaration.Substring(valueStart, valueEnd - valueStart), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int EffectiveZIndex(string css, params string[] classes)
    {
        var rules = System.Text.RegularExpressions.Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<declarations>[^{}]*)\}");
        (int Specificity, int SourceOrder, int ZIndex)? winner = null;

        for (var sourceOrder = 0; sourceOrder < rules.Count; sourceOrder++)
        {
            var rule = rules[sourceOrder];
            var zIndex = System.Text.RegularExpressions.Regex.Match(rule.Groups["declarations"].Value, @"z-index:\s*(?<value>\d+)\s*;");
            if (!zIndex.Success)
                continue;

            foreach (var selector in rule.Groups["selector"].Value.Split(','))
            {
                var selectorClasses = System.Text.RegularExpressions.Regex.Matches(selector, @"\.([\w-]+)")
                    .Select(match => match.Groups[1].Value)
                    .ToArray();
                if (!selectorClasses.All(required => classes.Contains(required, StringComparer.Ordinal)))
                    continue;

                var candidate = (Specificity: selectorClasses.Length, SourceOrder: sourceOrder, ZIndex: int.Parse(zIndex.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture));
                if (winner is null || candidate.Specificity > winner.Value.Specificity || candidate.Specificity == winner.Value.Specificity && candidate.SourceOrder >= winner.Value.SourceOrder)
                    winner = candidate;
            }
        }

        Assert.True(winner.HasValue, $"No z-index rule matches {string.Join('.', classes)}.");
        return winner!.Value.ZIndex;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Silksong Rando Logic Manager.slnx")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
