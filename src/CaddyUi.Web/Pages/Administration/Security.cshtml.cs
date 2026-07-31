using System.Text;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class SecurityModel : LocalizedPageModel
{
    private readonly AuthenticationStore _store;
    private readonly TotpService _totp;
    private readonly IDataProtector _protector;

    public SecurityModel(
        AuthenticationStore store,
        TotpService totp,
        IDataProtectionProvider dataProtectionProvider)
    {
        _store = store;
        _totp = totp;
        _protector = dataProtectionProvider.CreateProtector("CaddyUi.UserTotp.v1");
    }

    public bool TotpEnabled { get; private set; }

    [BindProperty]
    public string SetupSecret { get; set; } = string.Empty;

    [BindProperty]
    public string VerificationCode { get; set; } = string.Empty;

    public string ProvisioningUri { get; private set; } = string.Empty;

    public IReadOnlyList<string> RecoveryCodes { get; private set; } = Array.Empty<string>();

    [TempData]
    public string StatusMessage { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task OnPostBeginAsync()
    {
        await LoadAsync();
        SetupSecret = _totp.GenerateSecret();
        ProvisioningUri = _totp.BuildProvisioningUri(
            SetupSecret,
            User.Identity?.Name ?? "admin");
    }

    public async Task<IActionResult> OnPostEnableAsync()
    {
        await LoadAsync();
        if (!_totp.VerifyCode(SetupSecret, VerificationCode))
        {
            ModelState.AddModelError(string.Empty, "Der Bestätigungscode ist ungültig.");
            ProvisioningUri = _totp.BuildProvisioningUri(
                SetupSecret,
                User.Identity?.Name ?? "admin");
            return Page();
        }

        var userId = User.RequireUserId();
        var protectedSecret = Encoding.UTF8.GetBytes(_protector.Protect(SetupSecret));
        RecoveryCodes = _totp.GenerateRecoveryCodes();
        await _store.SetTotpAsync(userId, protectedSecret, enabled: true, HttpContext.RequestAborted);
        await _store.ReplaceRecoveryCodesAsync(
            userId,
            RecoveryCodes.Select(_totp.HashRecoveryCode).ToArray(),
            HttpContext.RequestAborted);
        TotpEnabled = true;
        StatusMessage = "TOTP wurde aktiviert.";
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var userId = User.RequireUserId();
        await _store.SetTotpAsync(userId, encryptedSecret: null, enabled: false, HttpContext.RequestAborted);
        await _store.ReplaceRecoveryCodesAsync(userId, Array.Empty<string>(), HttpContext.RequestAborted);
        StatusMessage = "TOTP und alle Recovery-Codes wurden deaktiviert.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var user = await _store.FindUserByUsernameAsync(
            User.Identity?.Name ?? string.Empty,
            HttpContext.RequestAborted);
        TotpEnabled = user?.TotpEnabled == true;
    }
}
