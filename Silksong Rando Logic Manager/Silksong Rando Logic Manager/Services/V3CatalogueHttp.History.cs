using System.Globalization;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static void MapHistory(RouteGroupBuilder api)
    {
        api.MapGet("/history/epochs", (HttpContext context) => HistoryReadAsync(context, "epochs")).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/history/boundaries", (HttpContext context) => HistoryReadAsync(context, "boundaries")).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/history/record", (HttpContext context) => HistoryReadAsync(context, "record")).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/history/point", (HttpContext context) => HistoryReadAsync(context, "point")).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/history/diff", (HttpContext context) => HistoryReadAsync(context, "diff")).RequireAuthorization(AccessPolicies.Normal);
    }
    private static long HistoryOrdinal(string? text)
    {
        if (text is null || text.Length == 0 || text.Any(c => c is < '0' or > '9')
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            throw new RequestBindingException();
        return ordinal;
    }

    private static int HistoryLimit(string? text)
    {
        if (text is null) return 100;
        var value = HistoryOrdinal(text);
        return value is >= 1 and <= 500 ? (int)value : throw new RequestBindingException();
    }

    private static string HistoryText(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new RequestBindingException();
        return property.GetString()!;
    }

    private static Guid HistoryEpoch(JsonElement body)
    {
        if (!Guid.TryParse(HistoryText(body, "epochId"), out var epoch)) throw new RequestBindingException();
        return epoch;
    }

    private static HistorySelector HistoryPointSelector(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new RequestBindingException();
        var ordinal = HistoryText(body, "ordinal");
        _ = HistoryOrdinal(ordinal);
        return new(HistoryEpoch(body), ordinal, HistoryText(body, "tableName"), HistoryText(body, "recordKey"));
    }

    private static async Task HistoryReadAsync(HttpContext context, string capability)
    {
        HistoryQuery query;
        if (capability == "epochs")
        {
            var cursor = context.Request.Query["afterCursor"];
            var limit = context.Request.Query["limit"];
            if (cursor.Count > 1 || limit.Count > 1) throw new RequestBindingException();
            query = new(capability, After: cursor.Count == 0 ? null : HistoryOrdinal(cursor[0]), Limit: HistoryLimit(limit.Count == 0 ? null : limit[0]));
        }
        else
        {
            using var document = await ReadObjectAsync(context);
            var body = document.RootElement;
            if (capability == "point") query = new(capability, First: HistoryPointSelector(body));
            else if (capability == "diff")
            {
                if (!body.TryGetProperty("first", out var first) || !body.TryGetProperty("second", out var second)) throw new RequestBindingException();
                var left = HistoryPointSelector(first);
                var right = HistoryPointSelector(second);
                if (!left.SameRecordAs(right)) throw new RequestBindingException();
                query = new(capability, First: left, Second: right);
            }
            else
            {
                long? after = null;
                if (body.TryGetProperty("afterOrdinal", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
                {
                    if (cursor.ValueKind != JsonValueKind.String) throw new RequestBindingException();
                    after = HistoryOrdinal(cursor.GetString());
                }
                var limit = 100;
                if (body.TryGetProperty("limit", out var requested))
                {
                    if (requested.ValueKind != JsonValueKind.Number || !requested.TryGetInt32(out limit) || limit is < 1 or > 500)
                        throw new RequestBindingException();
                }
                query = new(capability, HistoryEpoch(body), capability == "record" ? HistoryText(body, "tableName") : null,
                    capability == "record" ? HistoryText(body, "recordKey") : null, after, limit);
            }
        }
        var token = context.RequestAborted;
        object result;
        using (var publication = await context.RequestServices.GetRequiredService<SharedPublicationCoordinator>().AcquireAsync(token))
        {
            await WorkspaceBinding.RequireAsync(context, context.Request.Headers[WorkspaceBinding.Header]);
            await context.RequestServices.GetRequiredService<LiveAccess>().RequireAsync(AccessPolicies.Normal, token);
            result = await context.RequestServices.GetRequiredService<RecordHistoryStore>().ReadAsync(query, token);
        }
        // No cache envelope, current Room lookup or freshness acknowledgement.
        await WriteAsync(context, 200, result);
    }
}
