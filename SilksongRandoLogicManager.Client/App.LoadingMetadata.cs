using System.Globalization;
using System.Reflection;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private static readonly string LoadingRelease = ReadLoadingRelease();
    private static readonly string LoadingBuildTime = DateTimeOffset.TryParseExact(
        ApplicationBuild.Version, "'app-'yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var builtUtc)
        ? builtUtc.ToString("yyMMdd HHmmss", CultureInfo.InvariantCulture)
        : "Build time unavailable";

    private static string ReadLoadingRelease()
    {
        var release = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(release)) return "Release unavailable";

        return $"v{release}";
    }
}
