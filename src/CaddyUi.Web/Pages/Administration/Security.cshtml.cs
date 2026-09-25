using System.Security.Cryptography;
using System.Text;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;

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
    public string VerificationCode { get; set; } = string.Empty;

    public string SetupQrCodeSvg { get; private set; } = string.Empty;

    public IReadOnlyList<string> RecoveryCodes { get; private set; } = Array.Empty<string>();

    [TempData]
    public string StatusMessage { get; set; } = string.Empty;

    [TempData]
    public string? PendingSetupSecret { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task OnPostBeginAsync()
    {
        await LoadAsync();
        var setupSecret = _totp.GenerateSecret();
        PendingSetupSecret = _protector.Protect(setupSecret);
        PrepareQrCode(setupSecret);
        PreventCachingSetup();
    }

    public async Task<IActionResult> OnPostEnableAsync()
    {
        await LoadAsync();
        var setupSecret = GetPendingSetupSecret();
        if (setupSecret is null)
        {
            ModelState.AddModelError(string.Empty, "Die TOTP-Einrichtung ist abgelaufen. Bitte starte sie erneut.");
            return Page();
        }

        if (!_totp.VerifyCode(setupSecret, VerificationCode))
        {
            ModelState.AddModelError(string.Empty, "Der Bestätigungscode ist ungültig.");
            PrepareQrCode(setupSecret);
            TempData.Keep(nameof(PendingSetupSecret));
            PreventCachingSetup();
            return Page();
        }

        var userId = User.RequireUserId();
        var protectedSecret = Encoding.UTF8.GetBytes(_protector.Protect(setupSecret));
        RecoveryCodes = _totp.GenerateRecoveryCodes();
        await _store.SetTotpAsync(userId, protectedSecret, enabled: true, HttpContext.RequestAborted);
        await _store.ReplaceRecoveryCodesAsync(
            userId,
            RecoveryCodes.Select(_totp.HashRecoveryCode).ToArray(),
            HttpContext.RequestAborted);
        TotpEnabled = true;
        PendingSetupSecret = null;
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

    private string? GetPendingSetupSecret()
    {
        if (string.IsNullOrWhiteSpace(PendingSetupSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(PendingSetupSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private void PrepareQrCode(string setupSecret)
    {
        var provisioningUri = _totp.BuildProvisioningUri(
            setupSecret,
            User.Identity?.Name ?? "admin");
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(provisioningUri, QRCodeGenerator.ECCLevel.Q);
        SetupQrCodeSvg = new SvgQRCode(data).GetGraphic(5);
    }

    private void PreventCachingSetup()
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
    }
}
