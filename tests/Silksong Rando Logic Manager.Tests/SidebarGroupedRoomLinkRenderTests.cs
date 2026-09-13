using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.Layout;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SidebarGroupedRoomLinkRenderTests
{
    [Fact]
    public async Task GroupedLinks_AbbreviateOnlyNonidenticalMatchingActiveAndArchivedNames()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"silksong-sidebar-links-{Guid.NewGuid():N}.db");
        TestContext? context = null;
        try
        {
            var groupId = Guid.NewGuid();
            var activeMatchingId = Guid.NewGuid();
            var activeNonmatchingId = Guid.NewGuid();
            var archivedMatchingId = Guid.NewGuid();
            var archivedNonmatchingId = Guid.NewGuid();
            var exactMatchingId = Guid.NewGuid();
            var ungroupedId = Guid.NewGuid();
            var emptyGroupId = Guid.NewGuid();
            var whitespaceGroupId = Guid.NewGuid();
            var emptyGroupRoomId = Guid.NewGuid();
            var whitespaceGroupRoomId = Guid.NewGuid();
            var factory = new Factory(databasePath);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.AddRange(
                    new RoomGroup { Id = groupId, FriendlyName = "Bone", SortOrder = 0 },
                    new Room { Id = activeMatchingId, RoomGroupId = groupId, FriendlyName = "bone   - East", ReferenceId = "active-match", SortOrder = 0 },
                    new Room { Id = activeNonmatchingId, RoomGroupId = groupId, FriendlyName = "Deepnest", ReferenceId = "active-nonmatch", SortOrder = 1 },
                    new Room { Id = archivedMatchingId, RoomGroupId = groupId, FriendlyName = "BONE\tWest", ReferenceId = "archived-match", SortOrder = 0, IsArchived = true },
                    new Room { Id = archivedNonmatchingId, RoomGroupId = groupId, FriendlyName = "Hive", ReferenceId = "archived-nonmatch", SortOrder = 1, IsArchived = true },
                    new Room { Id = exactMatchingId, RoomGroupId = groupId, FriendlyName = "bOnE", ReferenceId = "exact-match", SortOrder = 2 },
                    new Room { Id = ungroupedId, FriendlyName = "Bone Ungrouped", ReferenceId = "ungrouped", SortOrder = 0 },
                    new RoomGroup { Id = emptyGroupId, FriendlyName = string.Empty, SortOrder = 1 },
                    new RoomGroup { Id = whitespaceGroupId, FriendlyName = " \t ", SortOrder = 2 },
                    new Room { Id = emptyGroupRoomId, RoomGroupId = emptyGroupId, FriendlyName = "  Empty group room", ReferenceId = "empty-group-room", SortOrder = 0 },
                    new Room { Id = whitespaceGroupRoomId, RoomGroupId = whitespaceGroupId, FriendlyName = " \t Whitespace group room", ReferenceId = "whitespace-group-room", SortOrder = 0 });
                await db.SaveChangesAsync();
            }

            context = new TestContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(factory);
            context.Services.AddSingleton<LogicCatalogService>();
            context.Services.AddSingleton<WorkspaceChangeNotifier>();
            context.Services.AddSingleton<DiagnosticState>();
            context.Services.AddSingleton<SceneReviewProjectionService>();
            context.Services.AddSingleton<SceneClassificationService>();
            context.Services.AddScoped<SceneDumpParser>();
            context.Services.AddScoped<SceneImportService>();
            context.Services.AddScoped<TransitionInverseSetupState>();
            var component = context.Render(builder =>
            {
                builder.OpenComponent<NavMenu>(0);
                builder.CloseComponent();
            });
            var rendered = component.FindComponent<NavMenu>();

            Assert.Empty(rendered.FindAll(".tree-primary-actions #scene-upload-input"));
            Assert.Empty(rendered.FindAll(".sidebar-export-footer #scene-upload-input"));
            Assert.Single(rendered.FindAll(".sidebar-export-footer .scene-review-trigger"));
            Assert.True(rendered.Markup.IndexOf("scene-review-trigger", StringComparison.Ordinal) < rendered.Markup.IndexOf("class=\"sidebar-export-row\"", StringComparison.Ordinal));

            rendered.Find("button.group-label").Click();
            rendered.FindAll("button.group-label").ElementAt(1).Click();
            rendered.FindAll("button.group-label").ElementAt(2).Click();
            SetPrivateField(rendered.Instance, "showArchivedRooms", true);
            rendered.Render();

            AssertLink(rendered, activeMatchingId, "- East", "bone   - East");
            AssertLink(rendered, activeNonmatchingId, "Deepnest", "Deepnest");
            AssertLink(rendered, archivedMatchingId, "West", "BONE\tWest");
            AssertLink(rendered, archivedNonmatchingId, "Hive", "Hive");
            AssertLink(rendered, exactMatchingId, "bOnE", "bOnE");
            var ungroupedLink = rendered.Find($"a[href='rooms/{ungroupedId}']");
            Assert.Equal("Bone Ungrouped", ungroupedLink.TextContent);
            Assert.Null(ungroupedLink.GetAttribute("aria-label"));
            AssertLink(rendered, emptyGroupRoomId, "  Empty group room", "  Empty group room");
            AssertLink(rendered, whitespaceGroupRoomId, " \t Whitespace group room", " \t Whitespace group room");
        }
        finally
        {
            context?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static void AssertLink(IRenderedComponent<NavMenu> rendered, Guid roomId, string text, string accessibleName)
    {
        var link = rendered.Find($"a[href='rooms/{roomId}']");
        Assert.Equal(text, link.TextContent);
        Assert.Equal(accessibleName, link.GetAttribute("aria-label"));
    }

    private static void SetPrivateField(object instance, string name, object value) =>
        instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class Factory(string databasePath) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
    }
}
