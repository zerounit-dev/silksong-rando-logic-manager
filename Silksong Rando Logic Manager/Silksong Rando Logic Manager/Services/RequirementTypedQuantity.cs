using System.Globalization;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Raw quantity boundaries for the two typed countable shapes only; legacy forms do not use them.</summary>
internal static class RequirementTypedQuantity
{
    internal static bool TryRead(string remainder, out int quantity, out string countable)
    {
        var text = remainder.TrimStart();
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var token = text[..end];
        countable = text[end..].Trim();
        quantity = 0;
        return token.Length > 0 && token[0] is >= '1' and <= '9' &&
            token.All(character => character is >= '0' and <= '9') &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out quantity) &&
            countable.Length > 0;
    }

    internal static bool TryReadLast(string remainder, out int quantity, out string countable)
    {
        var text = remainder.Trim();
        var start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var token = text[start..];
        countable = text[..start].TrimEnd();
        quantity = 0;
        return countable.Length > 0 && token.Length > 0 && token[0] is >= '1' and <= '9' &&
            token.All(character => character is >= '0' and <= '9') &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out quantity);
    }
}
