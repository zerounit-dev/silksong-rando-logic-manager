using Microsoft.EntityFrameworkCore;
#if DEBUG
using Microsoft.AspNetCore.Components.Server.Circuits;
#endif
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
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

if (isPortableRelease)
{
    builder.WebHost.UseUrls("http://localhost:5000");
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
#if DEBUG
var debugDiagnostics = new DebugDiagnosticSwitches();
builder.Services.AddSingleton(debugDiagnostics);
builder.Services.AddSingleton<DebugEfContextDiagnosticInterceptor>();
builder.Services.AddScoped<CircuitHandler, CircuitInboundTimingHandler>();
builder.Logging.AddDebugDiagnosticFilters(debugDiagnostics);
#endif

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "data", "silksong-rando-logic.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
#if DEBUG
builder.Services.AddLogicDbContextFactory(databasePath);
#else
builder.Services.AddDbContextFactory<LogicDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
#endif
builder.Services.AddScoped<LogicReferenceResolver>();
builder.Services.AddScoped<LogicCatalogService>();
builder.Services.AddSingleton<RequirementCatalogueWriteCoordinator>();
builder.Services.AddScoped<RequirementCatalogueService>();
builder.Services.AddScoped<RequirementCatalogueExportService>();
builder.Services.AddSingleton<RequirementCatalogueImportParser>();
builder.Services.AddScoped<RequirementCatalogueImportService>();
builder.Services.AddScoped<RequirementValidationService>();
builder.Services.AddScoped<IRequirementValidationService>(provider => provider.GetRequiredService<RequirementValidationService>());
builder.Services.AddScoped<IRoomGraphExportService, RoomGraphExportService>();
builder.Services.AddScoped<ConnectionRoomSaveCoordinator>();
builder.Services.AddSingleton<LogicValidationService>();
builder.Services.AddScoped<SceneImportService>();
builder.Services.AddScoped<TransitionInverseSetupService>();
builder.Services.AddScoped<TransitionInverseSetupState>();
builder.Services.AddSingleton<SceneReviewProjectionService>();
builder.Services.AddScoped<SceneDumpParser>();
builder.Services.AddSingleton<SceneClassificationService>();
builder.Services.AddSingleton<MapManifestParser>();
builder.Services.AddScoped<MapManifestService>();
builder.Services.AddScoped<MapLinkService>();
builder.Services.AddScoped<MapOverlayService>();
builder.Services.AddScoped<AppliedRoomStatusService>();
builder.Services.AddScoped<IAppliedRoomStatusService>(provider => provider.GetRequiredService<AppliedRoomStatusService>());
builder.Services.AddSingleton<MapRenderProjectionService>();
builder.Services.AddSingleton<MapOverlayAssetCatalog>();
builder.Services.AddSingleton<MapOverlayPlacementService>();
builder.Services.AddScoped<AreaMapLoader>();
builder.Services.AddScoped<IAreaMapLoader>(provider => provider.GetRequiredService<AreaMapLoader>());
builder.Services.AddSingleton<SceneImageFileService>();
builder.Services.AddScoped<SceneImageCaptureService>();
builder.Services.AddSingleton<DiagnosticState>();
builder.Services.AddSingleton<WorkspaceChangeNotifier>();
builder.Services.AddScoped<RoomEditorV2LogicLoader>();
builder.Services.AddScoped<IRoomEditorV2LogicLoader>(provider => provider.GetRequiredService<RoomEditorV2LogicLoader>());
builder.Services.AddScoped<ISceneLayoutLoader, SceneLayoutLoader>();
builder.Services.AddScoped<IRoomEditorV2CommandService, RoomEditorV2CommandService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DistributedExportService>();
builder.Services.AddScoped<DistributedImportApplicationService>();
builder.Services.AddSingleton<DistributedImportPackageParser>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestDiagnostics").LogError(exception, "Unhandled server request exception.");
        context.RequestServices.GetRequiredService<DiagnosticState>().Show(exception.ToString());
        throw;
    }
});

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LogicDbContext>>();
    await using var dbContext = await dbContextFactory.CreateDbContextAsync();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!isPortableRelease)
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/data/scenes/{roomId:guid}.webp", (Guid roomId, SceneImageFileService sceneImages) =>
{
    var stream = sceneImages.OpenRead(roomId);
    return stream is null ? Results.NotFound() : Results.File(stream, "image/webp");
});
app.MapGet("/data/scene-source/preview.webp", (SceneImageFileService sceneImages) =>
{
    var stream = sceneImages.OpenPreview();
    return stream is null ? Results.NotFound() : Results.File(stream, "image/webp");
});
app.MapLocalExchangeDownloadEndpoints();
app.MapLocalRequirementCatalogueDownloadEndpoints();
app.MapLocalRoomGraphDownloadEndpoints();
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
