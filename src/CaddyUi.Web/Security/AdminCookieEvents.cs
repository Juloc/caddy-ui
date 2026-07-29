using System.Security.Claims;
using CaddyUi.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CaddyUi.Web.Security;

public sealed class AdminCookieEvents : CookieAuthenticationEvents
{
    public const string SessionTokenClaim = "caddy_ui_session_token";

    private readonly AuthenticationStore _store;
    private readonly RequestSurfaceResolver _surfaceResolver;

    public AdminCookieEvents(
        AuthenticationStore store,
        RequestSurfaceResolver surfaceResolver)
    {
        _store = store;
        _surfaceResolver = surfaceResolver;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var surface = _surfaceResolver.GetResolved(context.HttpContext);
        var expectedScheme = surface == RequestSurface.PublicAdmin
            ? AuthenticationSchemes.PublicAdmin
            : AuthenticationSchemes.LanAdmin;
        if (!string.Equals(context.Scheme.Name, expectedScheme, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            return;
        }

        var token = context.Principal?.FindFirstValue(SessionTokenClaim);
        var userAgent = context.HttpContext.Request.Headers.UserAgent.ToString();
        var session = token is null
            ? null
            : await _store.ValidateAdminSessionAsync(
                token,
                userAgent,
                context.HttpContext.RequestAborted);
        if (session is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        var principalUserId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var principalUsername = context.Principal?.FindFirstValue(ClaimTypes.Name);
        var principalRole = context.Principal?.FindFirstValue(ClaimTypes.Role);
        if (!string.Equals(
                principalUserId,
                session.User.Id.ToString("D"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                principalUsername,
                session.User.Username,
                StringComparison.Ordinal) ||
            !string.Equals(
                principalRole,
                session.User.Role,
                StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var returnUrl = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect($"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

public static class AuthenticationSchemes
{
    public const string SmartAdmin = "caddy-ui-admin";
    public const string LanAdmin = "caddy-ui-admin-lan";
    public const string PublicAdmin = "caddy-ui-admin-public";
}
