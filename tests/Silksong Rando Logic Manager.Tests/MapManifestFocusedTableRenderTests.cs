using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapManifestFocusedTableRenderTests
{
    [Fact]
    public void ReviewModalStyles_UseViewportStableHeightAndSixteenByNinePreview()
    {
        var css = File.ReadAllText(FindAppCss());

            Assert.Contains("height: calc(100dvh - 2rem)", css);
            Assert.Contains("aspect-ratio: 16 / 9", css);
            Assert.Contains(".map-reconciliation-summary-viewport, .map-reconciliation-focused-viewport { flex: 1 1 auto; min-height: 0; overflow-x: hidden; overflow-y: auto; }", css);
            Assert.Contains(".map-reconciliation-table { width: 100%; max-width: 100%;", css);
            Assert.Contains("table-layout: fixed", css);
            Assert.Contains("overflow-wrap: anywhere; white-space: normal", css);
            Assert.Contains(".map-review-summary-zone-column { width: 22.9%; }", css);
            Assert.Contains(".map-review-summary-group-column { width: 30.5%; }", css);
            Assert.Contains(".map-review-focused-cache-key-column { width: 17.8%; }", css);
            Assert.Contains(".map-review-focused-current-column, .map-review-focused-proposed-column { width: 22.9%; }", css);
            Assert.DoesNotContain(".map-reconciliation-summary-table { min-width:", css);
            Assert.DoesNotContain(".map-reconciliation-focused-table { min-width:", css);
    }

    [Fact]
    public async Task Upload_AcceptsNonstandardFilenameAndReportsMalformedJson()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"silksong-map-upload-{Guid.NewGuid():N}.db");
        TestContext? context = null;
        try
        {
            context = new TestContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var factory = new Factory(databasePath);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            ConfigureMapLandingServices(context, factory);

            var rendered = context.Render(builder =>
            {
                builder.OpenComponent<MapLanding>(0);
                builder.CloseComponent();
            });
            var landing = rendered.FindComponent<MapLanding>();

            await UploadAsync(landing, new TestBrowserFile("downloaded-map-data", """{"gameMapRoot":{"name":"Map"},"zones":[],"mapScenes":[]}"""));

            Assert.Contains("zone change summary", landing.Markup);
            Assert.DoesNotContain("Select scene-map-manifest.latest.json.", landing.Markup);

            InvokePrivate(landing.Instance, "CloseReconciliationReview");
            await UploadAsync(landing, new TestBrowserFile("still-not-json", "{"));

            Assert.Contains("Could not apply map manifest:", landing.Markup);
            Assert.DoesNotContain("zone change summary", landing.Markup);
            await using var verification = factory.CreateDbContext();
            Assert.Empty(await verification.Maps.ToListAsync());
        }
        finally
        {
            context?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void ReviewModal_RendersSummaryThenFocusedZoneWithTableValidVirtualizedRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"silksong-map-modal-{Guid.NewGuid():N}.db");
        TestContext? context = null;
        try
        {
            context = new TestContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var factory = new Factory(databasePath);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            ConfigureMapLandingServices(context, factory);

            var rendered = context.Render(builder =>
            {
                builder.OpenComponent<MapLanding>(0);
                builder.CloseComponent();
            });
            var landing = rendered.FindComponent<MapLanding>();
            var rows = Enumerable.Range(0, 100).Select(index => new MapReconciliationPlanRow
            {
                Kind = MapReconciliationKind.Changed,
                Entity = MapReconciliationEntity.Chunk,
                Label = $"chunk {index}",
                ZoneInGameId = "Large",
                SceneInGameId = "Scene",
                CacheIndex = index,
                CurrentBounds = index == 1 ? new(0, 0, 3, 2) : new(0, 0, 1, 1),
                ProposedBounds = index == 1 ? new(0, 0, 1, 1) : new(0, 0, 3, 2)
            }).Append(new MapReconciliationPlanRow { Kind = MapReconciliationKind.Added, Entity = MapReconciliationEntity.Zone, Label = "added", ZoneInGameId = "Large", Selected = true })
              .Append(new MapReconciliationPlanRow { Kind = MapReconciliationKind.Removed, Entity = MapReconciliationEntity.Zone, Label = "removed", ZoneInGameId = "Large" })
              .Append(new MapReconciliationPlanRow { Kind = MapReconciliationKind.Unchanged, Entity = MapReconciliationEntity.Zone, Label = "unchanged", ZoneInGameId = "Large" })
              .ToList();
            var plan = new MapReconciliationPlan
            {
                Manifest = new("Map", [new("Large", new(0, 0, 2, 2))], [], 0),
                Rows = rows
            };
            var review = new MapManifestReviewState(plan);
            SetPrivateField(landing.Instance, "reconciliationPlan", plan);
            SetPrivateField(landing.Instance, "reviewState", review);
            landing.Render();

            Assert.Contains("zone change summary", landing.Markup);
            Assert.Contains("<th>Zone</th>", landing.Markup);
            Assert.Contains("map-reconciliation-summary-table", landing.Markup);
            Assert.Contains("map-review-summary-zone-column", landing.Markup);
            Assert.Contains("map-review-summary-group-column", landing.Markup);
            Assert.Contains("map-review-summary-unchanged-column", landing.Markup);
            Assert.DoesNotContain("previous zone", landing.Markup);

            var navigation = GetPrivateField<MapManifestReviewNavigationState>(landing.Instance, "reviewNavigation");
            Assert.True(navigation.OpenZone("Large"));
            review.FocusZone("Large");
            landing.Render();
            var markup = landing.Markup;

            Assert.Contains("review zone: Large", markup);
            Assert.Contains("map-reconciliation-focused-table", markup);
            Assert.Contains("map-review-focused-change-column", markup);
            Assert.Contains("map-review-focused-area-column", markup);
            Assert.Contains("map-review-focused-entity-column", markup);
            Assert.Contains("map-review-focused-cache-key-column", markup);
            Assert.Contains("map-review-focused-chunk-column", markup);
            Assert.Contains("map-review-focused-current-column", markup);
            Assert.Contains("map-review-focused-proposed-column", markup);
            Assert.Contains("map-review-focused-select-column", markup);
            Assert.Contains("<tbody data-map-review-virtualized-rows=\"true\">", markup);
            Assert.Contains("<tr style=\"height:", markup);
            Assert.DoesNotContain("<></>", markup);
            Assert.DoesNotContain("</>", markup);
            Assert.Single(landing.FindAll("[data-map-review-bounded-viewport='true']"));
            Assert.Single(landing.FindAll("th"), heading => heading.TextContent.StartsWith("Change"));
            Assert.StartsWith("Entity", landing.Find("th").TextContent);
            Assert.Single(landing.FindAll("th"), heading => heading.TextContent == "Area");
            Assert.Contains("previous zone", markup);
            Assert.Contains("next zone", markup);
            Assert.Contains("map-review-area-same", markup);
            Assert.Contains("map-review-area-grew", markup);
            Assert.Contains("map-review-area-shrank", markup);
            Assert.Contains("map-review-filter-all", markup);
            Assert.Contains("<label for=\"map-review-filter-Added\">Added</label>", markup);
            Assert.Contains("<label for=\"map-review-filter-Removed\">Removed</label>", markup);
            Assert.Contains("<label for=\"map-review-filter-Changed\">Changed</label>", markup);
            Assert.Contains("<label for=\"map-review-filter-Unchanged\">Unchanged</label>", markup);
            Assert.Contains("class=\"map-review-change-added\"", markup);
            Assert.Contains("class=\"map-review-change-removed\"", markup);
            Assert.Contains("class=\"map-review-change-changed\"", markup);
            Assert.Contains("class=\"map-review-change-unchanged\"", markup);
            Assert.DoesNotContain("select all", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aria-expanded", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aria-checked=\"mixed\"", markup, StringComparison.OrdinalIgnoreCase);

            review.SetRowSelected(rows[0], true);
            landing.Render();

            landing.Find("input[aria-label='Search zone changes']").Input("chunk 0");
            var matchingRow = landing.Find($"[data-map-review-row-id='{rows[0].Id}']");
            matchingRow.TriggerEvent("onmouseenter", new MouseEventArgs());
            Assert.Contains("map-review-hover", landing.Markup);
            landing.Find("#map-review-filter-Changed").Change(true);
            Assert.NotNull(landing.Find($"[data-map-review-row-id='{rows[0].Id}']"));
            Assert.NotNull(landing.Find($"[data-map-review-row-id='{rows[100].Id}']"));
            Assert.Equal("Zone", landing.Find($"[data-map-review-row-id='{rows[100].Id}'] td").TextContent);
            Assert.Contains("<strong>Zone</strong>", landing.Markup);
            Assert.Equal(rows[100].Id.ToString(), landing.FindAll("[data-map-review-row-id]").First().GetAttribute("data-map-review-row-id"));
            Assert.DoesNotContain($"data-map-review-row-id=\"{rows[101].Id}\"", landing.Markup);
        }
        finally
        {
            context?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static T GetPrivateField<T>(object instance, string name) => (T)instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetPrivateField(object instance, string name, object value) => instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void InvokePrivate(object instance, string name) => instance.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(instance, null);

    private static async Task UploadAsync(IRenderedComponent<MapLanding> landing, IBrowserFile file)
    {
        var upload = (Task)landing.Instance.GetType().GetMethod("UploadAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(landing.Instance, [new InputFileChangeEventArgs([file])])!;
        await upload;
        landing.Render();
    }

    private static string FindAppCss()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate the application stylesheet.");
    }

    private static void ConfigureMapLandingServices(TestContext context, IDbContextFactory<LogicDbContext> factory)
    {
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(factory);
        context.Services.AddSingleton(new MapManifestParser());
        context.Services.AddSingleton(new MapManifestService(factory));
        context.Services.AddSingleton(new MapOverlayService(factory));
        context.Services.AddSingleton(new MapRenderProjectionService());
        context.Services.AddSingleton(new MapOverlayAssetCatalog());
        context.Services.AddSingleton(new MapOverlayPlacementService());
            context.Services.AddScoped<AppliedRoomStatusService>(_ => new(fixture));
            context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton(new DiagnosticState());
    }

    private sealed class TestBrowserFile(string name, string content) : IBrowserFile
    {
        private readonly byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);

        public string Name { get; } = name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => bytes.Length;
        public string ContentType => "application/json";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize) throw new IOException("The uploaded file exceeds the allowed size.");
            return new MemoryStream(bytes, writable: false);
        }
    }

    private sealed class Factory(string databasePath) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
