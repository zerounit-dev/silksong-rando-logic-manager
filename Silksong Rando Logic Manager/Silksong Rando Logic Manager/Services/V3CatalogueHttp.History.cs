using System.Globalization;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static void MapHistory(RouteGroupBuilder api)
    {
        api.MapPost("/history/record", (HttpContext context) => HistoryReadAsync(context, "record")).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/history/time", (HttpContext context) => HistoryReadAsync(context, "time")).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/history/diff", (HttpContext context) => HistoryReadAsync(context, "diff")).RequireAuthorization(AccessPolicies.Normal);
    }

    private static long HistoryRowId(string? text)
    {
        if (text is null || text.Length == 0 || text.Any(c => c is < '0' or > '9')
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new RequestBindingException();
        return id;
    }

    private static string HistoryText(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) throw new RequestBindingException();
        return property.GetString()!;
    }

    private static HistorySelector HistoryVersionSelector(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new RequestBindingException();
        var rowId = HistoryText(body, "rowId");
        if (HistoryRowId(rowId) == 0) throw new RequestBindingException();
        return new(HistoryText(body, "tableName"), HistoryText(body, "recordKey"), rowId);
    }

    private static async Task HistoryReadAsync(HttpContext context, string capability)
    {
        using var document = await ReadObjectAsync(context);
        var body = document.RootElement;
        HistoryQuery query;
        if (capability == "diff")
        {
            if (!body.TryGetProperty("first", out var first) || !body.TryGetProperty("second", out var second)) throw new RequestBindingException();
            var left = HistoryVersionSelector(first); var right = HistoryVersionSelector(second);
            if (!left.SameRecordAs(right)) throw new RequestBindingException();
            query = new(capability, First: left, Second: right);
        }
        else
        {
            var table = HistoryText(body, "tableName"); var key = HistoryText(body, "recordKey");
            if (capability == "time")
            {
                if (!DateTime.TryParseExact(HistoryText(body, "atUtc"), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc)
                    || utc.Kind != DateTimeKind.Utc) throw new RequestBindingException();
                query = new(capability, table, key, AtUtc: utc);
            }
            else
            {
                long? after = null;
                if (body.TryGetProperty("afterRowId", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
                {
                    if (cursor.ValueKind != JsonValueKind.String) throw new RequestBindingException();
                    after = HistoryRowId(cursor.GetString());
                }
                var limit = 100;
                if (body.TryGetProperty("limit", out var requested)
                    && (requested.ValueKind != JsonValueKind.Number || !requested.TryGetInt32(out limit) || limit is < 1 or > 500)) throw new RequestBindingException();
                query = new(capability, table, key, after, limit);
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
        await WriteAsync(context, 200, result);
    }
}
