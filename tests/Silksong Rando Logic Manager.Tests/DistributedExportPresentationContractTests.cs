using Bunit;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class DistributedExportPresentationContractTests
{
    [Fact]
    public void SidebarExport_IsStickyFooterWithOnlyUncheckedMapOptionAndNoGroupSelector()
    {
        var root = FindSolutionRoot();
        var nav = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Layout", "NavMenu.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));
        var modalStart = nav.IndexOf("<section class=\"confirmation-modal sidebar-export-modal\"", StringComparison.Ordinal);
        var modal = nav.Substring(modalStart, nav.IndexOf("</section>", modalStart, StringComparison.Ordinal) - modalStart);

        Assert.Contains("<footer class=\"sidebar-export-footer\">", nav);
        Assert.Contains("class=\"room-tree\"", nav);
        Assert.Contains("include area map (only recommended for master copy)", modal);
        Assert.Contains("includeAreaMap = false;", nav);
        Assert.Equal(1, Count(modal, "<input"));
        Assert.DoesNotContain("<select", modal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<datalist", modal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grid-template-rows: auto minmax(0, 1fr) auto", css);
        Assert.Contains(".room-tree { min-height: 0; overflow-y: auto", css);
        Assert.Contains(".sidebar-export-footer", css);
    }

    [Fact]
    public void V2Action_IsRightmostImmediateExportOnTheSoleRoomEditor()
    {
        using var context = new TestContext();
        var invoked = false;
        var component = context.RenderComponent<RoomLifecycleContentControlsPresentation>(p => p
            .Add(x => x.View, new RoomLifecycleContentControlsView(false, false))
            .Add(x => x.CurrentRoomExport, () => invoked = true));
        var buttons = component.FindAll("button").ToArray();

        Assert.Equal("room-current-export", buttons[^1].Id);
        Assert.Equal("export current room", buttons[^1].GetAttribute("aria-label"));
        Assert.Equal("export room", buttons[^1].TextContent.Trim());
        Assert.True(buttons[^1].QuerySelector("i")!.ClassList.Contains("fa-file-export"));
        buttons[^1].Click();
        Assert.True(invoked);
        var root = FindSolutionRoot();
        var nav = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Layout", "NavMenu.razor"));
        var page = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "RoomEditorV2Page.razor"));
        Assert.False(File.Exists(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Pages", "Home.razor")));
        Assert.DoesNotContain("@inject DistributedExportService", nav);
        Assert.DoesNotContain("@inject DistributedExportService", page);
    }

    private static string FindSolutionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Silksong Rando Logic Manager.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Solution root was not found.");
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0; index += needle.Length) count++;
        return count;
    }
}
