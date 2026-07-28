using CaddyUi.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Portal;

[AllowAnonymous]
public sealed class AuthorizeModel : PageModel
{
    private readonly AuthenticationStore _store;

    public AuthorizeModel(AuthenticationStore store)
    {
        _store = store;
    }

    public async Task<IActionResult> OnGetAsync(Guid group, string? returnTo = null)
    {
        var accessGroup = await _store.FindAccessGroupAsync(group, HttpContext.RequestAborted);
        if (accessGroup is null)
        {
            return NotFound();
        }

        var cookieName = PortalCookieName(group);
        var token = Request.Cookies[cookieName] ?? string.Empty;
        var session = await _store.ValidatePortalSessionAsync(
            group,
            token,
            Request.Headers.UserAgent.ToString(),
            HttpContext.RequestAborted);
        if (session is not null)
        {
            Response.Headers["Remote-User"] = session.Username;
            Response.Headers["X-Caddy-Portal-User"] = session.Username;
            return StatusCode(StatusCodes.Status200OK);
        }

        var safeReturnTo = SafeReturnTo(returnTo ?? Request.Headers["X-Forwarded-Uri"].ToString());
        return Redirect(
            $"/__caddy_ui_auth/login?group={group:D}&returnTo={Uri.EscapeDataString(safeReturnTo)}");
    }

    public static string PortalCookieName(Guid groupId)
    {
        return $"caddy_ui_portal_{groupId:N}";
    }

    public static string SafeReturnTo(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        return candidate.StartsWith("/", StringComparison.Ordinal) &&
            !candidate.StartsWith("//", StringComparison.Ordinal) &&
            !candidate.Contains('\\')
                ? candidate
                : "/";
    }
}
