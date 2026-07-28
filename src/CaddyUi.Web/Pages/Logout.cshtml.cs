using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages;

[Authorize]
public sealed class LogoutModel : PageModel
{
    private readonly AuthenticationStore _store;

    public LogoutModel(AuthenticationStore store)
    {
        _store = store;
    }

    public IActionResult OnGet()
    {
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var token = User.FindFirst(AdminCookieEvents.SessionTokenClaim)?.Value;
        if (!string.IsNullOrWhiteSpace(token))
        {
            await _store.RevokeAdminSessionAsync(token, HttpContext.RequestAborted);
        }

        await HttpContext.SignOutAsync(AuthenticationSchemes.LanAdmin);
        await HttpContext.SignOutAsync(AuthenticationSchemes.PublicAdmin);
        return RedirectToPage("/Login");
    }
}
