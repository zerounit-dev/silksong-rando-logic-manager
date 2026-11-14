using Microsoft.AspNetCore.Routing;

namespace Silksong_Rando_Logic_Manager.Services;

// The root client owns only UI paths. Non-file matching alone cannot protect
// extensionless server endpoints or missing requests within their namespaces.
public sealed class ClientUiRouteConstraint : IRouteConstraint
{
    private static readonly string[] ServerNamespaces =
    [
        "/v2", "/tools", "/api", "/exports", "/data", "/account",
        "/room-image-drafts", "/room-image-originals", "/_blazor",
        "/_framework", "/_content", "/lib", "/images", "/Components"
    ];

    public static bool IsClientUiPath(PathString path) =>
        !ServerNamespaces.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey,
        RouteValueDictionary values, RouteDirection routeDirection) =>
        httpContext is not null && IsClientUiPath(httpContext.Request.Path);
}
