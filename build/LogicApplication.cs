using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.AspNetCore.StaticWebAssets.Tasks;

// Build-time linking only. There is no browser manifest or mutable resolver.
public sealed class GenerateLogicExecutableGraph : Microsoft.Build.Utilities.Task
{
    [Required] public string Root { get; set; }
    [Required] public string OutputRoot { get; set; }
    [Required] public string Identity { get; set; }
    [Output] public ITaskItem[] Assets { get; set; }
    [Output] public ITaskItem[] Endpoints { get; set; }
    [Output] public ITaskItem[] SourceAssets { get; set; }
    [Output] public ITaskItem[] Modules { get; set; }

    static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    static string RelativePath(string root, string path)
    {
        var directory = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        return Uri.UnescapeDataString(directory.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }
    public override bool Execute()
    {
        try
        {
            Root = Path.GetFullPath(Root); OutputRoot = Path.GetFullPath(OutputRoot);
            var client = Path.Combine(Root, "SilksongRandoLogicManager.Client", "wwwroot");
            var server = Path.Combine(Root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot");
            var names = new[] { "js/cacheDiagnostics.mjs", "js/localLoadTiming.mjs", "js/logicCacheProtocol.mjs", "js/logicCacheQueries.mjs",
                "lib/sqlite/sqlite3.wasm", "lib/sqlite/sqlite3.mjs", "workspace-request.js", "requirements.js", "room-export.js", "room-exchange.js",
                "js/logicCacheBroker.mjs", "js/logicCacheOwner.mjs", "js/logicCache.mjs", "catalogue.js", "sidebar-drag.js",
                "scene-import-page.js", "scene-preview.js", "room-audit-storage.js", "room-audit-switcher.js", "room-audit-scene.js",
                "map-support.js", "editor.js", "requirement-catalogue.js", "map-navigation.js", "archipelago.js" };
            var sources = names.ToDictionary(n => n, n => File.Exists(Path.Combine(client, n)) ? Path.Combine(client, n) : Path.Combine(server, n), StringComparer.Ordinal);
            if (!Regex.IsMatch(Identity, "\\Aapp-[0-9]{21}\\z")) throw new InvalidOperationException("Invalid application build stamp.");
            var identity = Identity;
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            var linked = new List<ITaskItem>(); var endpoints = new List<ITaskItem>();
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var dependency = new Regex("(?<q>['\"])(?<path>\\.\\.?/[^'\"]+\\.(?:m?js|wasm))\\k<q>");
            Func<string, string> link = null;
            link = name => {
                if (urls.ContainsKey(name)) return urls[name];
                if (!sources.ContainsKey(name) || !visiting.Add(name)) throw new InvalidOperationException("Unresolved/cyclic executable dependency: " + name);
                var bytes = File.ReadAllBytes(sources[name]);
                if (!name.EndsWith(".wasm", StringComparison.Ordinal))
                {
                    var text = Encoding.UTF8.GetString(bytes);
                    // The vendor module's WASM loading is explicitly pinned by its caller's locateFile option.
                    if (name != "lib/sqlite/sqlite3.mjs") text = dependency.Replace(text, match => {
                        var resolved = RelativePath(client, Path.GetFullPath(Path.Combine(client, Path.GetDirectoryName(name) ?? "", match.Groups["path"].Value))).Replace('\\', '/');
                        return match.Groups["q"].Value + link(resolved) + match.Groups["q"].Value;
                    });
                    if (name == "js/logicCache.mjs" || name == "js/logicCacheBroker.mjs" || name == "js/logicCacheOwner.mjs")
                        text = "const applicationVersion = '" + identity + "';\n" + text;
                    if (name == "js/logicCacheOwner.mjs") text = text.Replace("__LOGIC_SQLITE_WASM_URL__", link("lib/sqlite/sqlite3.wasm"));
                    bytes = Encoding.UTF8.GetBytes(text);
                }
                // Content distinction is a physical storage concern, not a custom URL fingerprint.
                var directory = Path.Combine(OutputRoot, "code", Hash(bytes));
                var physical = Path.Combine(directory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(physical));
                if (!File.Exists(physical)) File.WriteAllBytes(physical, bytes);
                var candidate = new TaskItem(physical); candidate.SetMetadata("RelativePath", "logic-code/" + name);
                var pattern = new TaskItem("LogicExecutable"); pattern.SetMetadata("Pattern", "*" + Path.GetExtension(name)); pattern.SetMetadata("Expression", "#[.{fingerprint}]!");
                var contentType = new TaskItem(name.EndsWith(".wasm", StringComparison.Ordinal) ? "application/wasm" : "text/javascript");
                contentType.SetMetadata("Pattern", "*"); contentType.SetMetadata("Priority", "0");
                var define = new DefineStaticWebAssets { BuildEngine = BuildEngine, CandidateAssets = new ITaskItem[] { candidate },
                    SourceId = "SilksongRandoLogicManager.Client", SourceType = "Computed", BasePath = "/", ContentRoot = directory + Path.DirectorySeparatorChar,
                    FingerprintCandidates = true, FingerprintPatterns = new ITaskItem[] { pattern } };
                if (!define.Execute()) throw new InvalidOperationException("Native asset definition failed: " + name);
                var defineEndpoints = new DefineStaticWebAssetEndpoints { BuildEngine = BuildEngine, CandidateAssets = define.Assets, ContentTypeMappings = new ITaskItem[] { contentType } };
                if (!defineEndpoints.Execute()) throw new InvalidOperationException("Native endpoint definition failed: " + name);
                var resolve = new ResolveFingerprintedStaticWebAssetEndpointsForAssets { BuildEngine = BuildEngine,
                    CandidateAssets = define.Assets, CandidateEndpoints = defineEndpoints.Endpoints, IsStandalone = false };
                if (!resolve.Execute() || resolve.ResolvedEndpoints.Length != 1) throw new InvalidOperationException("Native URL resolution failed: " + name);
                var url = "/" + resolve.ResolvedEndpoints[0].ItemSpec.TrimStart('/');
                if (url == "/logic-code/" + name) throw new InvalidOperationException("Executable asset did not receive a native fingerprint: " + name);
                linked.AddRange(define.Assets); endpoints.AddRange(defineEndpoints.Endpoints);
                urls.Add(name, url); visiting.Remove(name); return url;
            };
            foreach (var name in names) link(name);
            Modules = urls.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => {
                var module = new TaskItem(x.Key); module.SetMetadata("Url", x.Value); return (ITaskItem)module;
            }).ToArray();
            Assets = linked.ToArray(); Endpoints = endpoints.ToArray(); SourceAssets = sources.Values.Select(x => (ITaskItem)new TaskItem(x)).ToArray();
            Log.LogMessage(MessageImportance.High, "Logic shared executable graph generated: {0} assets; identity {1}", Assets.Length, identity);
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
    }
}

// Tiny project-specific compilation input; never reads, hashes or links assets.
public sealed class EmitLogicApplicationSource : Microsoft.Build.Utilities.Task
{
    [Required] public string OutputRoot { get; set; }
    [Required] public string Identity { get; set; }
    [Required] public string ProjectName { get; set; }
    [Required] public ITaskItem[] Modules { get; set; }
    public bool Interop { get; set; }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text, new UTF8Encoding(false));
    }
    public override bool Execute()
    {
        try
        {
            if (!Regex.IsMatch(Identity, "\\Aapp-[0-9]{21}\\z")) throw new InvalidOperationException("Invalid application build stamp.");
            var urls = Modules.ToDictionary(x => x.ItemSpec, x => x.GetMetadata("Url"), StringComparer.Ordinal);
            var identity = Identity;
            var generatedNamespace = "LogicApplicationGenerated_" + Regex.Replace(ProjectName, "[^A-Za-z0-9_]", "_");
            var code = "// Generated from the shared build stamp and native SDK asset task outputs.\nglobal using " + generatedNamespace + ";\nnamespace " + generatedNamespace + " {\ninternal static class ApplicationBuild { internal const string Version = \"" + identity + "\"; }\n";
            code += "internal static class ApplicationModules { internal static string Url(string name) => name switch {\n";
            code += string.Join("\n", urls.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => "\"" + x.Key + "\" => \"" + x.Value + "\","));
            code += "\n_ => throw new System.ArgumentOutOfRangeException(nameof(name)) };\n";
            if (Interop) code += "internal static async System.Threading.Tasks.Task<Microsoft.JSInterop.IJSObjectReference> ImportAsync(Microsoft.JSInterop.IJSRuntime js, string name, System.Action failure = null) { try { return await js.InvokeAsync<Microsoft.JSInterop.IJSObjectReference>(\"import\", new object[] { Url(name) }); } catch (Microsoft.JSInterop.JSException) { failure?.Invoke(); throw; } }\n";
            code += "}\n}\n";
            Write(Path.Combine(OutputRoot, "ApplicationBuild.g.cs"), code);
            Write(Path.Combine(OutputRoot, "urls.txt"), string.Join("\n", urls.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)) + "\n");
            Log.LogMessage(MessageImportance.High, "Logic application source emitted: {0}; identity {1}", ProjectName, identity);
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
    }
}
