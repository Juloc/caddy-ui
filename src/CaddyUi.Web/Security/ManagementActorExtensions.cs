using System.Security.Claims;
using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Web.Security;

public static class ManagementActorExtensions
{
    public static ManagementActor ToManagementActor(
        this ClaimsPrincipal principal,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(context);
        return new ManagementActor(
            principal.RequireUserId(),
            principal.Identity?.Name ?? "unknown",
            context.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
    }
}
