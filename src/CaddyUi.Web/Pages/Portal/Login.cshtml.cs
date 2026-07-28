using System.ComponentModel.DataAnnotations;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Portal;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private readonly AuthenticationStore _store;
    private readonly LoginProtectionService _protection;
    private readonly PasswordHashService _passwords;
    private readonly RequestSurfaceResolver _surfaceResolver;
    private readonly AccessGroupStateStore _groupState;

    public LoginModel(
        AuthenticationStore store,
        LoginProtectionService protection,
        PasswordHashService passwords,
        RequestSurfaceResolver surfaceResolver,
        AccessGroupStateStore groupState)
    {
        _store = store;
        _protection = protection;
        _passwords = passwords;
        _surfaceResolver = surfaceResolver;
        _groupState = groupState;
    }

    [BindProperty(SupportsGet = true)]
    public Guid Group { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ReturnTo { get; set; } = "/";

    [BindProperty]
    public PortalLoginInput Input { get; set; } = new();

    public string GroupName { get; private set; } = "Geschützter Zugriff";

    public async Task<IActionResult> OnGetAsync()
    {
        ReturnTo = AuthorizeModel.SafeReturnTo(ReturnTo);
        return await LoadGroupAsync() ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ReturnTo = AuthorizeModel.SafeReturnTo(ReturnTo);
        if (!await LoadGroupAsync())
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var remoteAddress = _surfaceResolver.GetClientAddress(HttpContext);
        var scope = $"portal:{Group:D}";
        var protection = await _protection.EvaluateAsync(
            scope,
            Input.Username,
            remoteAddress,
            HttpContext.RequestAborted);
        if (!protection.Allowed)
        {
            ModelState.AddModelError(string.Empty, "Die Anmeldung ist vorübergehend gesperrt.");
            return Page();
        }

        var credential = await _store.FindPortalCredentialAsync(
            Group,
            Input.Username,
            HttpContext.RequestAborted);
        var verification = credential is null
            ? new PasswordVerificationResult(false)
            : _passwords.Verify(Input.Password, credential.PasswordHash);
        if (credential is null || !credential.Enabled || !verification.Succeeded)
        {
            await _protection.RecordFailureAsync(
                scope,
                Input.Username,
                remoteAddress,
                "invalid-credentials",
                HttpContext.RequestAborted);
            ModelState.AddModelError(string.Empty, "Benutzername oder Passwort ist ungültig.");
            return Page();
        }

        await _protection.RecordSuccessAsync(
            scope,
            Input.Username,
            remoteAddress,
            HttpContext.RequestAborted);
        var token = await _store.CreatePortalSessionAsync(
            credential,
            SessionLifetime,
            remoteAddress,
            Request.Headers.UserAgent.ToString(),
            HttpContext.RequestAborted);
        Response.Cookies.Append(
            AuthorizeModel.PortalCookieName(Group),
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.Add(SessionLifetime),
            });
        return LocalRedirect(ReturnTo);
    }

    private async Task<bool> LoadGroupAsync()
    {
        var group = await _store.FindAccessGroupAsync(Group, HttpContext.RequestAborted);
        if (group is null ||
            !await _groupState.IsEnabledAsync(Group, HttpContext.RequestAborted))
        {
            return false;
        }

        GroupName = group.Name;
        return true;
    }

    public sealed class PortalLoginInput
    {
        [Required]
        [MaxLength(200)]
        public string Username { get; set; } = string.Empty;

        [Required]
        [MaxLength(1024)]
        public string Password { get; set; } = string.Empty;
    }
}
