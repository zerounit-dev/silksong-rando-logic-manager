using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Authentication;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static async Task EditorHistoryReadAsync(HttpContext context, EditorHistoryRequest request)
    {
        if (!Enum.IsDefined(request.Family) || request.RecordId == Guid.Empty || request.Cursor is { } cursor
            && (cursor.BeforeUtc.Kind != DateTimeKind.Utc || cursor.BeforeRowId <= 0 || cursor.Ceiling < cursor.BeforeRowId))
            throw new RequestBindingException();

        var services = context.RequestServices;
        var token = context.RequestAborted;
        EditorHistoryPage result;
        using (await services.GetRequiredService<SharedPublicationCoordinator>().AcquireAsync(token))
        {
            await WorkspaceBinding.RequireAsync(context, context.Request.Headers[WorkspaceBinding.Header]);
            await services.GetRequiredService<LiveAccess>().RequireAsync(AccessPolicies.Normal, token);
            var accounts = services.GetRequiredService<AccountsService>();
            result = await services.GetRequiredService<RecordHistoryStore>().ReadEditorAsync(request,
                ids => accounts.ReadEditorHistoryNamesAsync(ids, token), async ids =>
                {
                    var factory = services.GetRequiredService<IDbContextFactory<LogicDbContext>>();
                    await using var db = await factory.CreateDbContextAsync(token);
                    return await db.RoomGroups.AsNoTracking().Where(group => ids.Contains(group.Id))
                        .Select(group => new { group.Id, group.FriendlyName })
                        .ToDictionaryAsync(group => group.Id, group => group.FriendlyName, token);
                }, token);
        }
        await WriteAsync(context, 200, result);
    }
}
