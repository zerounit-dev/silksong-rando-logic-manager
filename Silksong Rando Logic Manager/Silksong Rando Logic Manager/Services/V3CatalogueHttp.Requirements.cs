using System.Text;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Authentication;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static void MapRequirements(RouteGroupBuilder api)
    {
        api.MapGet("/requirements/export", async (HttpContext context, RequirementCatalogueExportService service) =>
        {
            var result = await service.ExportAsync(context.RequestAborted);
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{result.FileName}\"";
            await context.Response.Body.WriteAsync(result.JsonUtf8, context.RequestAborted);
        }).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/requirements/import/validate", (HttpContext context) => RequirementImportAsync(context, replace: false)).RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/requirements/import/replace", (HttpContext context) => RequirementImportAsync(context, replace: true)).RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/requirements/recheck", RequirementRecheckAsync).RequireAuthorization(AccessPolicies.Author);
        MapRequirementKind<ApplyRequirementPredicate>(api, "predicates", (s, c) => s.ApplyAsync(c, CancellationToken.None),
            (s, id) => s.DeleteAsync(new DeleteRequirementPredicate(id), CancellationToken.None),
            (s, c) => s.ReorderAsync(new ReorderRequirementPredicate(c.Id, c.TargetIndex), CancellationToken.None));
        MapRequirementKind<ApplyRequirementCrest>(api, "crests", (s, c) => s.ApplyAsync(c, CancellationToken.None),
            (s, id) => s.DeleteAsync(new DeleteRequirementCrest(id), CancellationToken.None),
            (s, c) => s.ReorderAsync(new ReorderRequirementCrest(c.Id, c.TargetIndex), CancellationToken.None));
        MapRequirementKind<ApplyRequirementMethod>(api, "methods", (s, c) => s.ApplyAsync(c, CancellationToken.None),
            (s, id) => s.DeleteAsync(new DeleteRequirementMethod(id), CancellationToken.None),
            (s, c) => s.ReorderAsync(new ReorderRequirementMethod(c.Id, c.TargetIndex), CancellationToken.None));
        MapRequirementKind<ApplyRequirementItem>(api, "items", (s, c) => s.ApplyAsync(c, CancellationToken.None),
            (s, id) => s.DeleteAsync(new DeleteRequirementItem(id), CancellationToken.None),
            (s, c) => s.ReorderAsync(new ReorderRequirementItem(c.Id, c.TargetIndex), CancellationToken.None));
        MapRequirementKind<ApplyRequirementCountable>(api, "countables", (s, c) => s.ApplyAsync(c, CancellationToken.None),
            (s, id) => s.DeleteAsync(new DeleteRequirementCountable(id), CancellationToken.None),
            (s, c) => s.ReorderAsync(new ReorderRequirementCountable(c.Id, c.TargetIndex), CancellationToken.None));
        MapRequirementKind<ApplyRequirementSoul>(api, "souls", (s, c) => s.ApplyAsync(c, CancellationToken.None),
            (s, id) => s.DeleteAsync(new DeleteRequirementSoul(id), CancellationToken.None),
            (s, c) => s.ReorderAsync(new ReorderRequirementSoul(c.Id, c.TargetIndex), CancellationToken.None));
    }

    private static async Task RequirementImportAsync(HttpContext context, bool replace)
    {
        var services = context.RequestServices;
        var input = await ReadRequirementBodyAsync<Wire.RequirementImportText>(context);
        await services.GetRequiredService<LiveAccess>().RequireAsync(AccessPolicies.Author, context.RequestAborted);
        if (input.Json is null) throw new RequestBindingException();
        var parsed = services.GetRequiredService<RequirementCatalogueImportParser>().Parse(Encoding.UTF8.GetBytes(input.Json));
        if (!parsed.IsAccepted)
        {
            await WriteCacheResultAsync(context, 200, new Wire.RequirementOperationResult("validationRejected", [], string.Join("\n", parsed.Errors))); return;
        }
        if (!replace) { await WriteCacheResultAsync(context, 200, new Wire.RequirementOperationResult("committed", [])); return; }
        var result = await services.GetRequiredService<RequirementCatalogueImportService>().ReplaceConfirmedAsync(parsed.Snapshot!, CancellationToken.None);
        await WriteCacheResultAsync(context, 200, new Wire.RequirementOperationResult(Status(result.Status.ToString()), result.ValidationIssues, result.Message,
            Shared: result.Shared is { } shared ? new(null, shared) : null));
    }

    private static async Task RequirementRecheckAsync(HttpContext context, RequirementValidationService validation, SharedCatalogueService shared)
    {
        try
        {
            var result = await validation.RecheckAllForClientAsync(shared, CancellationToken.None);
            await WriteCacheResultAsync(context, 200, new Wire.RequirementOperationResult("committed", [], Counts: new(result.Successful, result.Failed, result.Unknown), Shared: result.Shared));
        }
        catch (RequirementValidationOperationException exception) when (!context.Response.HasStarted)
        { await WriteCacheResultAsync(context, 200, new Wire.RequirementOperationResult("failed", [], exception.Message)); }
    }

    private static void MapRequirementKind<T>(RouteGroupBuilder api, string kind,
        Func<RequirementCatalogueService, T, Task<RequirementCatalogueCommandOutcome>> apply,
        Func<RequirementCatalogueService, Guid, Task<RequirementCatalogueCommandOutcome>> delete,
        Func<RequirementCatalogueService, Wire.RequirementOrder, Task<RequirementCatalogueCommandOutcome>> reorder)
    {
        // Only transport binding is request-cancellable. Once admitted, the existing
        // coordinated transaction completes independently of HTTP ownership.
        api.MapPost($"/requirements/{kind}/apply", async (HttpContext context, RequirementCatalogueService catalogue) =>
        {
            await RequirementOutcomeAsync(context, await apply(catalogue, await ReadRequirementBodyAsync<T>(context)));
        }).RequireAuthorization(AccessPolicies.Author);
        api.MapPost($"/requirements/{kind}/delete", async (HttpContext context, RequirementCatalogueService catalogue) =>
        {
            var input = await ReadRequirementBodyAsync<Wire.RequirementIdentity>(context);
            if (input.Id == Guid.Empty) throw new RequestBindingException();
            await RequirementOutcomeAsync(context, await delete(catalogue, input.Id));
        }).RequireAuthorization(AccessPolicies.Author);
        api.MapPost($"/requirements/{kind}/reorder", async (HttpContext context, RequirementCatalogueService catalogue) =>
        {
            var input = await ReadRequirementBodyAsync<Wire.RequirementOrder>(context);
            if (input.Id == Guid.Empty) throw new RequestBindingException();
            await RequirementOutcomeAsync(context, await reorder(catalogue, input));
        }).RequireAuthorization(AccessPolicies.Author);
    }

    private static async Task RequirementOutcomeAsync(HttpContext context, RequirementCatalogueCommandOutcome outcome)
    {
        await WriteCacheResultAsync(context, 200, new Wire.RequirementOperationResult(Status(outcome.Status.ToString()), outcome.ValidationIssues, outcome.Message,
            Shared: outcome.Shared is { } sharedPatch ? new(null, sharedPatch) : null));
    }

    private static string Status(string value) => value switch
    { "Committed" => "committed", "ValidationRejected" => "validationRejected", "Missing" => "missing", "Failed" => "failed", _ => throw new InvalidOperationException("Unknown catalogue outcome.") };

    private static async Task<T> ReadRequirementBodyAsync<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new BadHttpRequestException("Expected JSON.", 415);
        // JSON escaping can use six bytes per source byte. Bound the envelope as
        // well as letting the strict parser enforce the decoded UTF-8 4 MiB cap.
        const int maximum = RequirementCatalogueImportParser.MaximumImportBytes * 6 + 1024;
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            if (bytes.Length + read > maximum) throw new RequestBindingException();
            bytes.Write(buffer, 0, read);
        }
        try { return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), Json) ?? throw new RequestBindingException(); }
        catch (JsonException) { throw new RequestBindingException(); }
    }
}
