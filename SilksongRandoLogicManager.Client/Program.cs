using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SilksongRandoLogicManager.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<SessionAccess>();
builder.Services.AddScoped<ClientRuntime>();
builder.Services.AddScoped(sp => new HttpClient(new SessionHttpHandler(sp.GetRequiredService<SessionAccess>(), sp.GetRequiredService<ClientRuntime>())
    { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ConfirmedCatalogue>();
builder.Services.AddScoped<LogicBrowserCache>();
builder.Services.AddScoped<MapDataOwner>();
builder.Services.AddScoped<Silksong_Rando_Logic_Manager.Services.SceneReviewProjectionService>();

await builder.Build().RunAsync();
