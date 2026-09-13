namespace Silksong_Rando_Logic_Manager.Services;

public static class RequirementCatalogueValidator
{
    public static RequirementDefinitionDraftValidation ValidateDraft(ApplyRequirementPredicate draft)
    {
        var syntax = RequirementCatalogueLanguage.TryParsePersistedValue(draft.InputSyntax, out var parsed)
            ? parsed
            : (RequirementInputSyntax)(-1);
        return new(Validate(
            [new RequirementCataloguePredicateValidation(draft.Id ?? Guid.Empty, draft.Name, draft.Category, syntax, draft.OutputSyntax, draft.Notes, draft.AliasDraft)],
            []));
    }

    public static RequirementDefinitionDraftValidation ValidateDraft(ApplyRequirementItem draft) => new(Validate(
        [],
        [new RequirementCatalogueItemValidation(draft.Id ?? Guid.Empty, draft.Name, draft.Category, draft.OutputValue, draft.Notes, draft.AliasDraft)]));

    public static IReadOnlyList<RequirementCatalogueValidationIssue> Validate(
        IEnumerable<RequirementCataloguePredicateValidation> predicates,
        IEnumerable<RequirementCatalogueItemValidation> items)
    {
        var issues = new List<RequirementCatalogueValidationIssue>();
        var predicateList = predicates.ToArray();
        var itemList = items.ToArray();

        foreach (var predicate in predicateList)
        {
            ValidateCommon(predicate.Id, predicate.Name, predicate.Notes, predicate.Aliases, issues);
            if (!Enum.IsDefined(predicate.InputSyntax))
            {
                issues.Add(Issue("InputSyntax", predicate.Id, predicate.Name, "Input syntax is not supported."));
            }
            else
            {
                ValidateOutputSyntax(predicate.Id, predicate.Name, predicate.InputSyntax, predicate.OutputSyntax, issues);
            }
        }

        foreach (var item in itemList)
        {
            ValidateCommon(item.Id, item.Name, item.Notes, item.Aliases, issues);
            if (string.IsNullOrWhiteSpace(item.OutputValue))
            {
                issues.Add(Issue("OutputValue", item.Id, item.Name, "Output value is required."));
            }
        }

        ValidateAliasNamespace(predicateList.Select(value => new DefinitionAliases(value.Id, value.Name, value.Aliases)), issues);
        ValidateAliasNamespace(itemList.Select(value => new DefinitionAliases(value.Id, value.Name, value.Aliases)), issues);
        return issues;
    }

    private static void ValidateCommon(Guid id, string? name, string? notes, string? aliases, List<RequirementCatalogueValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name)) issues.Add(Issue("Name", id, name, "Name is required."));
        if (notes is null) issues.Add(Issue("Notes", id, name, "Notes must not be null."));
        if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(aliases, out _, out _, out var error)) issues.Add(Issue("Aliases", id, name, error!));
    }

    private static void ValidateOutputSyntax(Guid id, string? name, RequirementInputSyntax inputSyntax, string? outputSyntax, List<RequirementCatalogueValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(outputSyntax))
        {
            issues.Add(Issue("OutputSyntax", id, name, "Output syntax is required."));
            return;
        }

        if (!RequirementOutputTemplate.TryTokenize(outputSyntax, out var tokens, out var error))
        {
            issues.Add(Issue("OutputSyntax", id, name, error!));
            return;
        }

        var required = RequirementCatalogueLanguage.GetSupportedOutputArguments(inputSyntax);
        var seen = new HashSet<RequirementOutputPlaceholder>();
        foreach (var placeholder in tokens.OfType<RequirementOutputPlaceholderToken>().Select(token => token.Value))
        {
            if (!required.Contains(placeholder))
            {
                issues.Add(Issue("OutputSyntax", id, name, $"Output placeholder '{ToPlaceholderText(placeholder)}' is unavailable for this input syntax."));
            }
            else if (!seen.Add(placeholder))
            {
                issues.Add(Issue("OutputSyntax", id, name, $"Output placeholder '{ToPlaceholderText(placeholder)}' appears more than once."));
            }
        }

        foreach (var placeholder in required.Where(placeholder => !seen.Contains(placeholder)))
        {
            issues.Add(Issue("OutputSyntax", id, name, $"Output placeholder '{ToPlaceholderText(placeholder)}' is required exactly once."));
        }
    }

    private static void ValidateAliasNamespace(IEnumerable<DefinitionAliases> definitions, List<RequirementCatalogueValidationIssue> issues)
    {
        var aliasesByIdentity = new Dictionary<string, DefinitionAliases>();
        foreach (var definition in definitions)
        {
            if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(definition.Aliases, out _, out var aliases, out _)) continue;
            foreach (var alias in aliases)
            {
                var identity = RequirementCatalogueLanguage.NormalizeIdentification(alias);

                if (aliasesByIdentity.TryGetValue(identity, out var existing))
                {
                    issues.Add(new RequirementCatalogueValidationIssue("Aliases", definition.Id, definition.Name, existing.Id, existing.Name, "Alias duplicates an alias in this namespace."));
                    continue;
                }

                aliasesByIdentity.Add(identity, definition);
            }
        }
    }

    private static RequirementCatalogueValidationIssue Issue(string field, Guid id, string? name, string message) =>
        new(field, id, name, null, null, message);

    private static string ToPlaceholderText(RequirementOutputPlaceholder placeholder) => $"{{{placeholder.ToString().ToLowerInvariant()}}}";

    private sealed record DefinitionAliases(Guid Id, string? Name, string? Aliases);
}
