using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
#if DEBUG
using Microsoft.AspNetCore.Components.Server.Circuits;
#endif
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Silksong_Rando_Logic_Manager.Services.HttpLogging;
using Silksong_Rando_Logic_Manager.Authentication;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;

var portableReleaseDirectory = AppContext.BaseDirectory;
var isPortableRelease = File.Exists(Path.Combine(portableReleaseDirectory, "portable-release.marker"));
var builder = isPortableRelease
    ? WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = portableReleaseDirectory
    })
    : WebApplication.CreateBuilder(args);

// Finalize the unified graph before authentication/hosting validation or consumers.
var storagePaths = ApplicationStoragePaths.Configure(builder, args);
var authenticationMode = AuthenticationMode.Read(builder.Configuration);
builder.AddHttpIntegration(); // Validate hosting before any startup writes.
builder.AddConfiguredAuthentication(authenticationMode, storagePaths);
builder.AddHttpRequestLogging();
Silksong_Rando_Logic_Manager.Services.Publishing.DocumentationPublishingService.Register(builder);

if (isPortableRelease)
{
    builder.WebHost.UseUrls("http://localhost:5000");
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteOptions>(options =>
    options.ConstraintMap["clientUi"] = typeof(ClientUiRouteConstraint));
#if DEBUG
var debugDiagnostics = new DebugDiagnosticSwitches();
builder.Services.AddSingleton(debugDiagnostics);
builder.Services.AddSingleton<DebugEfContextDiagnosticInterceptor>();
builder.Services.AddScoped<CircuitHandler, CircuitInboundTimingHandler>();
builder.Logging.AddDebugDiagnosticFilters(debugDiagnostics);
#endif

var databasePath = storagePaths.LogicDatabase;
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
#if DEBUG
builder.Services.AddLogicDbContextFactory(databasePath);
#else
builder.Services.AddDbContextFactory<LogicDbContext>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()));
#endif
builder.Services.AddScoped<LogicReferenceResolver>();
builder.Services.AddScoped<LogicCatalogService>();
builder.Services.AddScoped<SharedCatalogueService>();
builder.Services.AddSingleton<RequirementCatalogueWriteCoordinator>();
builder.Services.AddSingleton<SharedPublicationCoordinator>();
builder.Services.AddSingleton<RecordHistoryStore>();
builder.Services.AddScoped<LogicCacheTransfer>();
builder.Services.AddScoped<RequirementCatalogueService>();
builder.Services.AddScoped<RequirementCatalogueExportService>();
builder.Services.AddSingleton<RequirementCatalogueImportParser>();
builder.Services.AddScoped<RequirementCatalogueImportService>();
builder.Services.AddScoped<RequirementValidationService>();
builder.Services.AddScoped<IRequirementValidationService>(provider => provider.GetRequiredService<RequirementValidationService>());
builder.Services.AddScoped<IRoomGraphExportService, RoomGraphExportService>();
builder.Services.AddScoped<IRoomGeometryExportService, RoomGeometryExportService>();
builder.Services.AddSingleton<LogicValidationService>();
builder.Services.AddScoped<SceneImportService>();
builder.Services.AddScoped<TransitionInverseSetupService>();
builder.Services.AddSingleton<SceneReviewProjectionService>();
builder.Services.AddScoped<SceneDumpParser>();
builder.Services.AddSingleton<SceneClassificationService>();
builder.Services.AddSingleton<MapManifestParser>();
builder.Services.AddScoped<MapManifestService>();
builder.Services.AddScoped<MapLinkService>();
builder.Services.AddScoped<MapOverlayService>();
builder.Services.AddSingleton<MapOverlayAssetCatalog>();
builder.Services.AddSingleton<MapOverlayPlacementService>();
builder.Services.AddSingleton<SceneImageFileService>();
builder.Services.AddScoped<SceneImageCaptureService>();
builder.Services.AddScoped<RoomImageOperationsService>();
builder.Services.AddScoped<DiagnosticState>();
builder.Services.AddScoped<ISceneLayoutLoader, SceneLayoutLoader>();
builder.Services.AddScoped<RoomAuditSnapshotService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RoomEditingTracker>();
builder.Services.AddScoped<RoomEditingDocumentLoader>();
builder.Services.AddScoped<RoomEditingHeaderCommands>();
builder.Services.AddScoped<RoomEditingHeaderProjection>();
builder.Services.AddScoped<RoomEditingHeaderWorkflow>();
builder.Services.AddScoped<RoomEditingSubroomCommands>();
builder.Services.AddScoped<RoomEditingCreationCommands>();
builder.Services.AddScoped<RoomEditingMutationProjection>();
builder.Services.AddScoped<RoomEditingRecovery>();
builder.Services.AddScoped<RoomEditingImages>();
builder.Services.AddScoped<RoomEditingWorkflow>();
builder.Services.AddScoped<DistributedExportService>();
builder.Services.AddScoped<DistributedImportApplicationService>();
builder.Services.AddSingleton<DistributedImportPackageParser>();

var app = builder.Build();
// Publication admission exists during startup; cache run identity is initialized
// only after migrations and changed-only canonical completion backfill commit.
_ = app.Services.GetRequiredService<SharedPublicationCoordinator>();

app.UseHttpRequestLogging();
app.UseHttpIntegration();
app.UseRouting();
app.UseConfiguredAccess();
app.UseRateLimiter();
app.UseAuthorization();
app.UseApplicationVersionAdmission();

await app.InitializeAccountsAsync();
await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LogicDbContext>>();
    await using var dbContext = await dbContextFactory.CreateDbContextAsync();
    await dbContext.Database.MigrateAsync();
    using var publication = await app.Services.GetRequiredService<SharedPublicationCoordinator>().AcquireAsync();
    await using var initialization = await dbContext.Database.BeginTransactionAsync();
    var completion = await LogicCompletionMaintenance.InitializeAsync(dbContext, CancellationToken.None);
    var manifest = await LogicCacheSql.ReadSchemaAsync(
        (Microsoft.Data.Sqlite.SqliteConnection)dbContext.Database.GetDbConnection(),
        (Microsoft.Data.Sqlite.SqliteTransaction)initialization.GetDbTransaction(), CancellationToken.None);
    await initialization.CommitAsync();
    publication.InitializeCache(manifest, completion);
    // Dispose the committed initialization transaction before pinning the observed baseline.
    await initialization.DisposeAsync();
    await publication.InitializeRecordHistoryAsync(app.Services.GetRequiredService<RecordHistoryStore>(), dbContext, manifest);
}

// Configure the HTTP request pipeline.
// Keep ordinary server/static status handling independent of SPA admission.
// Only the relocated WASM framework branch retains its terminal missing-file 404.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api") && !AuthenticationRegistration.IsFrameworkDisconnect(context.Request) && (!context.Request.Path.StartsWithSegments("/_framework")
    || context.Request.Path.StartsWithSegments("/_framework/blazor.web.js")
    || (context.Request.Path.Value?.StartsWith("/_framework/blazor.web.", StringComparison.OrdinalIgnoreCase) ?? false)
    || context.Request.Path.StartsWithSegments("/_framework/blazor.server.js")
    || (context.Request.Path.Value?.StartsWith("/_framework/blazor.server.", StringComparison.OrdinalIgnoreCase) ?? false)), branch =>
    branch.UseStatusCodePagesWithReExecute("/tools/not-found", createScopeForStatusCodePages: true));

// WASM's framework handler has a terminal, non-rejoining branch. The server
// script families (including fingerprinted/compressed names) belong to MapStaticAssets,
// not that branch. Both bootstraps and the server hub keep their root URLs.
// Selected assets must reach the endpoint executor, not this terminal branch.
app.UseWhen(context => context.GetEndpoint()?.RequestDelegate is null
    && !context.Request.Path.StartsWithSegments("/_framework/blazor.web.js")
    && !(context.Request.Path.Value?.StartsWith("/_framework/blazor.web.", StringComparison.OrdinalIgnoreCase) ?? false)
    && !context.Request.Path.StartsWithSegments("/_framework/blazor.server.js")
    && !(context.Request.Path.Value?.StartsWith("/_framework/blazor.server.", StringComparison.OrdinalIgnoreCase) ?? false),
    branch => branch.UseBlazorFrameworkFiles());
app.UseStaticFiles();
app.UseAntiforgery();
app.MapAccountHttp();
app.MapV3Catalogue();

app.MapFallbackToFile("{*path:nonfile:clientUi}", "index.html");

app.MapStaticAssets();
app.MapGet("/data/scenes/{roomId:guid}.webp", async (Guid roomId, SceneImageFileService sceneImages, HttpContext context) =>
{
    context.Response.RegisterForDispose(await sceneImages.AcquireLifecycleAsync(context.RequestAborted));
    await context.RequestServices.GetRequiredService<LiveAccess>().RequireAsync(token: context.RequestAborted);
    var stream = sceneImages.OpenRead(roomId);
    return stream is null ? Results.NotFound() : Results.File(stream, "image/webp");
});
app.MapGet("/data/scene-source/preview.webp", (SceneImageFileService sceneImages) =>
{
    var stream = sceneImages.OpenPreview();
    return stream is null ? Results.NotFound() : Results.File(stream, "image/webp");
});
app.MapGet("/room-image-drafts/{handle:guid}", async (Guid handle, SceneImageFileService images, HttpContext context) =>
{
    context.Response.RegisterForDispose(await images.AcquireLifecycleAsync(context.RequestAborted));
    var image = await images.OpenStagedPreviewAsync(context.RequestServices.GetRequiredService<LiveAccess>(), handle);
    return image is null ? Results.NotFound() : Results.File(image, "image/webp");
});
app.MapGet("/room-image-originals/{roomId:guid}", async (Guid roomId, string? v, SceneImageFileService images, IDbContextFactory<LogicDbContext> contexts, HttpContext context) =>
{
    context.Response.RegisterForDispose(await images.AcquireLifecycleAsync(context.RequestAborted));
    await context.RequestServices.GetRequiredService<LiveAccess>().RequireAsync(token: context.RequestAborted);
    await using var db = await contexts.CreateDbContextAsync();
    var hash = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId).Select(x => x.SceneImageOriginalHash).SingleOrDefaultAsync();
    var path = images.GetOriginalPath(roomId);
    if (v is null || v != hash || !SceneImageFileService.Matches(path, hash)) return Results.NotFound();
    return Results.File(SceneImageFileService.PreviewBytes(path), "image/webp");
});
app.MapLocalExchangeDownloadEndpoints();
app.MapLocalRequirementCatalogueDownloadEndpoints();
app.MapLocalRoomGraphDownloadEndpoints();
app.MapLocalRoomGeometryDownloadEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (!isPortableRelease)
{
    app.Run();
    return;
}

try
{
    await app.StartAsync();
}
catch (Exception exception) when (IsAddressAlreadyInUse(exception))
{
    Console.Error.WriteLine("Silksong Rando Logic Manager could not start because http://localhost:5000 is already in use. Close the application using port 5000 and try again.");
    await app.DisposeAsync();
    return;
}

try
{
    Process.Start(new ProcessStartInfo("http://localhost:5000") { UseShellExecute = true });
}
catch (Exception exception)
{
    app.Logger.LogError(exception, "The portable server started but the default browser could not be opened.");
}

await app.WaitForShutdownAsync();

static bool IsAddressAlreadyInUse(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
        if (current is Win32Exception { NativeErrorCode: 10048 }) return true;
    }

    return false;
}
