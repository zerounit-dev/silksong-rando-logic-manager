using Microsoft.Extensions.Primitives;

namespace Silksong_Rando_Logic_Manager.Authentication;

// A31: account consistency for an existing document, not a credential or permission.
internal static class WorkspaceBinding
{
    internal const string Header = "X-Logic-Workspace";
    internal const string FormField = "__LogicWorkspace";
    internal static async Task RequireAsync(HttpContext context, StringValues supplied)
    {
        var identity = await context.RequestServices.GetRequiredService<LiveAccess>().CurrentAsync(context.RequestAborted);
        if (supplied.Count != 1 || string.IsNullOrEmpty(supplied[0]) || identity is null ||
            !string.Equals(supplied[0], identity.WorkspaceKey, StringComparison.Ordinal))
        {
            throw new AccessDeniedException("authentication_required", 401,
                "The account changed or sign-in is required. No operation was admitted.");
        }
    }
}
