namespace SilksongRandoLogicManager.Contracts;

public static class ApplicationVersion
{
    public const string Header = "X-Logic-App-Version";
    public const string MismatchCode = "application_version_mismatch";
    public static string Current => ApplicationBuild.Version;
    public static bool Valid(string? value) => value is { Length: 25 } && value.StartsWith("app-", StringComparison.Ordinal)
        && value.AsSpan(4).IndexOfAnyExcept("0123456789") < 0;
}
