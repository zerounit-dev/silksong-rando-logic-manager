using Microsoft.Extensions.Configuration.Json;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Resolved process paths, not a second configuration provider or store lifecycle.</summary>
public sealed class ApplicationStoragePaths
{
    public string ConfigDirectory { get; }
    public string DataRoot { get; }
    public string LogicDatabase => Path.Combine(DataRoot, "silksong-rando-logic.db");

    private ApplicationStoragePaths(string configDirectory, string dataRoot)
    {
        ConfigDirectory = configDirectory;
        DataRoot = dataRoot;
    }

    public static ApplicationStoragePaths Configure(WebApplicationBuilder builder, string[] args)
    {
        // The file cannot locate itself. Only explicit unprefixed environment/CLI
        // selects the locator; native host/content-root selection has already run.
        using var locator = new ConfigurationManager();
        locator.AddEnvironmentVariables().AddCommandLine(args);
        var config = Path.GetFullPath(locator["LogicManager:ConfigDirectory"] ?? "Config", builder.Environment.ContentRootPath);
        // Retire the native root JSON providers rather than retaining a competing
        // settings graph. Keep host fallback, Development user secrets and all
        // native services/content-root behavior supplied by CreateBuilder.
        var rootFiles = builder.Configuration.Sources.OfType<JsonConfigurationSource>()
            .Where(source => source.Path == "appsettings.json"
                || source.Path == $"appsettings.{builder.Environment.EnvironmentName}.json").ToArray();
        var fileIndex = rootFiles.Length == 0 ? builder.Configuration.Sources.Count
            : builder.Configuration.Sources.IndexOf(rootFiles[0]);
        var reloadOnChange = builder.Configuration.GetValue("hostBuilder:reloadConfigOnChange", true);
        foreach (var source in rootFiles)
        {
            builder.Configuration.Sources.Remove(source);
        }
        // Insert at the native app-file position, before user secrets/env/CLI.
        // External selection replaces local Config files; it is not an overlay
        // on the source checkout's authentication mode.
        foreach (var name in new[] { "appsettings.json", $"appsettings.{builder.Environment.EnvironmentName}.json" })
        {
            var source = new JsonConfigurationSource
            {
                Path = Path.Combine(config, name), Optional = true, ReloadOnChange = reloadOnChange
            };
            source.ResolveFileProvider();
            builder.Configuration.Sources.Insert(fileIndex++, source);
        }
        builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
        var paths = new ApplicationStoragePaths(config,
            Path.GetFullPath(builder.Configuration["Storage:DataRoot"] ?? "Storage", builder.Environment.ContentRootPath));
        builder.Services.AddSingleton(paths);
        return paths;
    }

    internal bool IsPrivateWebPath(PathString requestPath, string? webRoot)
    {
        if (string.IsNullOrEmpty(webRoot)) return false;
        requestPath = new PathString(requestPath.Value?.Replace('\\', '/'));
        // A deliberately selected directory under wwwroot must not become a
        // static-file escape hatch. No whole config/state file provider is added.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in new[] { ConfigDirectory, DataRoot })
        {
            var relative = Path.GetRelativePath(webRoot, root);
            if (relative == ".") return true;
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)) continue;
            if (requestPath.StartsWithSegments("/" + relative.Replace(Path.DirectorySeparatorChar, '/'), comparison)) return true;
        }
        return false;
    }
}
