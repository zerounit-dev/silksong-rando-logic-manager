using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace SilksongRandoLogicManager.Contracts;

/// <summary>Format rules for the authored Alias of room transitions and connections only.</summary>
public static class RoomAuthoredAlias
{
    public const int MaxLength = 3; // UTF-16 code units, matching the existing editor limit.

    public static string Commit(string value, string? original = null) => value == original ? value : value.Trim();

    public static bool HasHyphen(string value) => value.Contains('-');
    public static bool HasOuterWhitespace(string value) => value.Length != 0
        && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    public static bool HasInternalWhitespace(string value)
    {
        foreach (var character in value.AsSpan().Trim())
            if (char.IsWhiteSpace(character)) return true;
        return false;
    }

    public static V2Severity Severity(string value) => string.IsNullOrWhiteSpace(value) ? V2Severity.Warning
        : value.Length > MaxLength || HasHyphen(value) || HasOuterWhitespace(value) || HasInternalWhitespace(value)
            ? V2Severity.Danger : V2Severity.Neutral;
}
