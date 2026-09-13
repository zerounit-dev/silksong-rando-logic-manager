using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Pure strict reader for the standalone requirement-catalogue V1 document.</summary>
public sealed class RequirementCatalogueImportParser
{
    public const int MaximumImportBytes = 4 * 1024 * 1024;

    public RequirementCatalogueImportParseResult Parse(ReadOnlyMemory<byte> jsonUtf8)
    {
        if (jsonUtf8.Length > MaximumImportBytes)
            return RequirementCatalogueImportParseResult.Rejected("The catalogue document exceeds the 4 MiB limit.");

        try
        {
            using var document = JsonDocument.Parse(jsonUtf8);
            var root = document.RootElement;
            RequireObject(root, "$", ["requirementCatalogueVersion", "predicates", "items"]);
            var version = RequiredInt(root, "requirementCatalogueVersion", "$");
            if (version != 1) throw Invalid("$.requirementCatalogueVersion must equal 1.");
            var predicates = ParsePredicates(RequiredArray(root, "predicates", "$"));
            var items = ParseItems(RequiredArray(root, "items", "$"));
            ValidateSnapshot(predicates, items);
            return RequirementCatalogueImportParseResult.Accepted(new(predicates, items));
        }
        catch (JsonException exception)
        {
            return RequirementCatalogueImportParseResult.Rejected($"Malformed JSON: {exception.Message}");
        }
        catch (RequirementCatalogueParseException exception)
        {
            return RequirementCatalogueImportParseResult.Rejected(exception.Message);
        }
    }

    private static IReadOnlyList<RequirementCatalogueExchangePredicate> ParsePredicates(JsonElement array)
    {
        var values = new List<RequirementCatalogueExchangePredicate>();
        var index = 0;
        foreach (var value in array.EnumerateArray())
        {
            var path = $"$.predicates[{index++}]";
            RequireObject(value, path, ["id", "name", "category", "inputSyntax", "outputSyntax", "notes", "sortOrder", "aliases"]);
            values.Add(new(RequiredGuid(value, "id", path), RequiredString(value, "name", path), RequiredNullableString(value, "category", path),
                RequiredString(value, "inputSyntax", path), RequiredString(value, "outputSyntax", path), RequiredString(value, "notes", path),
                RequiredInt(value, "sortOrder", path), RequiredString(value, "aliases", path)));
        }
        return values;
    }

    private static IReadOnlyList<RequirementCatalogueExchangeItem> ParseItems(JsonElement array)
    {
        var values = new List<RequirementCatalogueExchangeItem>();
        var index = 0;
        foreach (var value in array.EnumerateArray())
        {
            var path = $"$.items[{index++}]";
            RequireObject(value, path, ["id", "name", "category", "outputValue", "notes", "sortOrder", "aliases"]);
            values.Add(new(RequiredGuid(value, "id", path), RequiredString(value, "name", path), RequiredNullableString(value, "category", path),
                RequiredString(value, "outputValue", path), RequiredString(value, "notes", path), RequiredInt(value, "sortOrder", path),
                RequiredString(value, "aliases", path)));
        }
        return values;
    }

    private static void ValidateSnapshot(IReadOnlyList<RequirementCatalogueExchangePredicate> predicates, IReadOnlyList<RequirementCatalogueExchangeItem> items)
    {
        ValidateOrder(predicates.Select(x => x.SortOrder), "$.predicates");
        ValidateOrder(items.Select(x => x.SortOrder), "$.items");
        var identities = new HashSet<Guid>();
        foreach (var id in predicates.Select(x => x.Id).Concat(items.Select(x => x.Id)))
            if (id == Guid.Empty || !identities.Add(id)) throw Invalid("Every catalogue GUID must be nonempty and unique across the document.");

        var issues = RequirementCatalogueValidator.Validate(
            predicates.Select(x => new RequirementCataloguePredicateValidation(x.Id, x.Name, x.Category,
                RequirementCatalogueLanguage.TryParsePersistedValue(x.InputSyntax, out var syntax) ? syntax : (RequirementInputSyntax)(-1), x.OutputSyntax, x.Notes,
                x.Aliases)),
            items.Select(x => new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes,
                 x.Aliases)));
        if (issues.Count != 0) throw Invalid($"Catalogue validation failed: {issues[0].Message}");

        // Exchange is a strict snapshot contract, unlike an Apply draft.  A
        // replacement must never quietly rewrite a sender's flat alias value.
        foreach (var aliases in predicates.Select(x => x.Aliases).Concat(items.Select(x => x.Aliases)))
        {
            if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(aliases, out var canonical, out _, out _) ||
                !string.Equals(aliases, canonical, StringComparison.Ordinal))
            {
                throw Invalid("Catalogue aliases must use the canonical comma-and-space form.");
            }
        }
    }

    private static void ValidateOrder(IEnumerable<int> values, string path)
    {
        var index = 0;
        foreach (var value in values)
        {
            if (value != index) throw Invalid($"{path}[{index}].sortOrder must equal {index}.");
            index++;
        }
    }

    private static void RequireObject(JsonElement value, string path, IReadOnlyCollection<string> members)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{path} must be an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!found.Add(property.Name)) throw Invalid($"{path} contains duplicate member '{property.Name}'.");
            if (!members.Contains(property.Name)) throw Invalid($"{path} contains unknown member '{property.Name}'.");
        }
        foreach (var member in members)
            if (!found.Contains(member)) throw Invalid($"{path} is missing required member '{member}'.");
    }

    private static JsonElement Required(JsonElement value, string member, string path) => value.GetProperty(member);
    private static JsonElement RequiredArray(JsonElement value, string member, string path)
    {
        var child = Required(value, member, path);
        if (child.ValueKind != JsonValueKind.Array) throw Invalid($"{path}.{member} must be an array.");
        return child;
    }
    private static string RequiredString(JsonElement value, string member, string path)
    {
        var child = Required(value, member, path);
        if (child.ValueKind != JsonValueKind.String || child.GetString() is not { } text) throw Invalid($"{path}.{member} must be a string.");
        return text;
    }
    private static string? RequiredNullableString(JsonElement value, string member, string path)
    {
        var child = Required(value, member, path);
        if (child.ValueKind == JsonValueKind.Null) return null;
        if (child.ValueKind != JsonValueKind.String || child.GetString() is not { } text) throw Invalid($"{path}.{member} must be a string or null.");
        return text;
    }
    private static int RequiredInt(JsonElement value, string member, string path)
    {
        var child = Required(value, member, path);
        if (child.ValueKind != JsonValueKind.Number || !child.TryGetInt32(out var number)) throw Invalid($"{path}.{member} must be an integer.");
        return number;
    }
    private static Guid RequiredGuid(JsonElement value, string member, string path)
    {
        var text = RequiredString(value, member, path);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty) throw Invalid($"{path}.{member} must be a nonempty GUID in D format.");
        return id;
    }
    private static RequirementCatalogueParseException Invalid(string message) => new(message);
    private sealed class RequirementCatalogueParseException(string message) : Exception(message);
}
