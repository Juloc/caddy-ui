using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Localization;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel : LocalizedPageModel
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private readonly AuthenticationStore _store;
    private readonly UserPreferenceStore _preferences;
    private readonly LoginProtectionService _protection;
    private readonly PasswordHashService _passwords;
    private readonly TotpService _totp;
    private readonly IDataProtector _totpProtector;
    private readonly ITimeLimitedDataProtector _secondFactorProtector;
    private readonly RequestSurfaceResolver _surfaceResolver;
    private readonly UiCultureCatalog _cultures;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public LoginModel(
        AuthenticationStore store,
        UserPreferenceStore preferences,
        LoginProtectionService protection,
        PasswordHashService passwords,
        TotpService totp,
        IDataProtectionProvider dataProtectionProvider,
        RequestSurfaceResolver surfaceResolver,
        UiCultureCatalog cultures,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _preferences = preferences;
        _protection = protection;
        _passwords = passwords;
        _totp = totp;
        _totpProtector = dataProtectionProvider.CreateProtector("CaddyUi.UserTotp.v1");
        _secondFactorProtector = dataProtectionProvider
            .CreateProtector("CaddyUi.LoginSecondFactor.v1")
            .ToTimeLimitedDataProtector();
        _surfaceResolver = surfaceResolver;
        _cultures = cultures;
        _localizer = localizer;
    }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Restart { get; set; }

    [BindProperty]
    public SecondFactorInput SecondFactor { get; set; } = new();

    [TempData]
    public string? PendingSecondFactor { get; set; }

    public bool RequiresSecondFactor { get; private set; }

    public IActionResult OnGet()
    {
        var returnUrl = SafeReturnUrl(ReturnUrl);
        ReturnUrl = returnUrl;
        if (Restart)
        {
            PendingSecondFactor = null;
            return User.Identity?.IsAuthenticated == true
                ? LocalRedirect(returnUrl)
                : Page();
        }

        RequiresSecondFactor = TryGetSecondFactorChallenge(out _);
        if (RequiresSecondFactor)
        {
            TempData.Keep(nameof(PendingSecondFactor));
        }
        return User.Identity?.IsAuthenticated == true
            ? LocalRedirect(returnUrl)
            : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var returnUrl = SafeReturnUrl(ReturnUrl);
        ReturnUrl = returnUrl;
        ModelState.Remove(nameof(SecondFactor));
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var surface = _surfaceResolver.GetResolved(HttpContext);
        if (surface is not (RequestSurface.Lan or RequestSurface.PublicAdmin))
        {
            return NotFound();
        }

        var remoteAddress = _surfaceResolver.GetClientAddress(HttpContext);
        var identity = Input.Username.Trim();
        var protection = await _protection.EvaluateAsync(
            "admin",
            identity,
            remoteAddress,
            HttpContext.RequestAborted);
        if (!protection.Allowed)
        {
            Response.Headers.RetryAfter = Math.Ceiling(protection.RetryAfter.TotalSeconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            ModelState.AddModelError(string.Empty, _localizer["Login is temporarily blocked."]);
            return Page();
        }

        var user = await _store.FindUserByUsernameAsync(identity, HttpContext.RequestAborted);
        var verification = user is null
            ? new PasswordVerificationResult(false)
            : _passwords.Verify(Input.Password, user.PasswordHash);
        if (user is null || !user.Enabled || !verification.Succeeded)
        {
            await FailAsync(identity, remoteAddress, "invalid-credentials");
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(verification.UpgradedHash))
        {
            await _store.UpdatePasswordHashAsync(
                user.Id,
                verification.UpgradedHash,
                HttpContext.RequestAborted);
        }

        if (user.TotpEnabled)
        {
            CreateSecondFactorChallenge(user, returnUrl);
            return RedirectToPage(new { ReturnUrl = returnUrl });
        }

        await _protection.RecordSuccessAsync(
            "admin",
            identity,
            remoteAddress,
            HttpContext.RequestAborted);
        return await SignInAsync(user, surface, remoteAddress, returnUrl);
    }

    public async Task<IActionResult> OnPostSecondFactorAsync()
    {
        var returnUrl = SafeReturnUrl(ReturnUrl);
        ReturnUrl = returnUrl;
        ModelState.Clear();
        if (!TryValidateModel(SecondFactor, nameof(SecondFactor)))
        {
            RequiresSecondFactor = TryGetSecondFactorChallenge(out _);
            if (RequiresSecondFactor)
            {
                TempData.Keep(nameof(PendingSecondFactor));
            }

            return Page();
        }

        if (!TryGetSecondFactorChallenge(out var challenge))
        {
            ModelState.AddModelError(string.Empty, _localizer["The sign-in request has expired. Please sign in again."]);
            return Page();
        }

        returnUrl = SafeReturnUrl(challenge.ReturnUrl);
        ReturnUrl = returnUrl;
        PendingSecondFactor = null;
        var surface = _surfaceResolver.GetResolved(HttpContext);
        if (surface is not (RequestSurface.Lan or RequestSurface.PublicAdmin))
        {
            return NotFound();
        }

        var remoteAddress = _surfaceResolver.GetClientAddress(HttpContext);
        var protection = await _protection.EvaluateAsync(
            "admin",
            challenge.Username,
            remoteAddress,
            HttpContext.RequestAborted);
        if (!protection.Allowed)
        {
            Response.Headers.RetryAfter = Math.Ceiling(protection.RetryAfter.TotalSeconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            ModelState.AddModelError(string.Empty, _localizer["Login is temporarily blocked."]);
            return Page();
        }

        var user = await _store.FindUserByIdAsync(challenge.UserId, HttpContext.RequestAborted);
        if (user is null || !user.Enabled || !user.TotpEnabled ||
            !await VerifySecondFactorAsync(user, SecondFactor.Code))
        {
            await FailAsync(challenge.Username, remoteAddress, "invalid-second-factor");
            CreateSecondFactorChallenge(challenge, returnUrl);
            RequiresSecondFactor = true;
            return Page();
        }

        await _protection.RecordSuccessAsync(
            "admin",
            user.Username,
            remoteAddress,
            HttpContext.RequestAborted);
        return await SignInAsync(user, surface, remoteAddress, returnUrl);
    }

    private async Task<IActionResult> SignInAsync(
        UserAccount user,
        RequestSurface surface,
        string remoteAddress,
        string returnUrl)
    {
        var token = await _store.CreateAdminSessionAsync(
            user.Id,
            SessionLifetime,
            remoteAddress,
            Request.Headers.UserAgent.ToString(),
            HttpContext.RequestAborted);
        var language = _cultures.ResolvePreference(
            await _preferences.GetLanguageAsync(user.Id, HttpContext.RequestAborted));
        Response.Cookies.Append(
            UiCultureCatalog.LanguageCookieName,
            language,
            UserCultureMiddleware.CreateCookieOptions(HttpContext));

        var scheme = surface == RequestSurface.PublicAdmin
            ? AuthenticationSchemes.PublicAdmin
            : AuthenticationSchemes.LanAdmin;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("display_name", user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(UiCultureCatalog.LanguageClaimType, language),
            new Claim(AdminCookieEvents.SessionTokenClaim, token),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        await HttpContext.SignInAsync(
            scheme,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime),
            });

        return LocalRedirect(returnUrl);
    }

    private async Task<bool> VerifySecondFactorAsync(
        UserAccount user,
        string? candidate)
    {
        candidate = candidate?.Trim();
        if (user.TotpSecretEncrypted is null || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Contains('-'))
        {
            return await _store.ConsumeRecoveryCodeAsync(
                user.Id,
                _totp.HashRecoveryCode(candidate),
                HttpContext.RequestAborted);
        }

        try
        {
            var protectedSecret = Encoding.UTF8.GetString(user.TotpSecretEncrypted);
            var secret = _totpProtector.Unprotect(protectedSecret);
            return _totp.VerifyCode(secret, candidate);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private void CreateSecondFactorChallenge(UserAccount user, string returnUrl)
    {
        CreateSecondFactorChallenge(
            new LoginSecondFactorChallenge(user.Id, user.Username, returnUrl),
            returnUrl);
    }

    private void CreateSecondFactorChallenge(LoginSecondFactorChallenge challenge, string returnUrl)
    {
        challenge = challenge with { ReturnUrl = returnUrl };
        PendingSecondFactor = _secondFactorProtector.Protect(
            JsonSerializer.Serialize(challenge),
            TimeSpan.FromMinutes(5));
        ReturnUrl = returnUrl;
    }

    private bool TryGetSecondFactorChallenge(out LoginSecondFactorChallenge challenge)
    {
        challenge = default!;
        if (string.IsNullOrWhiteSpace(PendingSecondFactor))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<LoginSecondFactorChallenge>(
                _secondFactorProtector.Unprotect(PendingSecondFactor));
            if (parsed is null)
            {
                return false;
            }

            challenge = parsed;
            return challenge.UserId != Guid.Empty && !string.IsNullOrWhiteSpace(challenge.Username);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private async Task FailAsync(string identity, string remoteAddress, string reason)
    {
        await _protection.RecordFailureAsync(
            "admin",
            identity,
            remoteAddress,
            reason,
            HttpContext.RequestAborted);
        ModelState.AddModelError(
            string.Empty,
            _localizer["Username, password, or security code is invalid."]);
    }

    private string SafeReturnUrl(string? value)
    {
        return Url.IsLocalUrl(value) ? value! : "/";
    }

    public sealed class LoginInput
    {
        [Required]
        [MaxLength(200)]
        public string Username { get; set; } = string.Empty;

        [Required]
        [MaxLength(1024)]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class SecondFactorInput
    {
        [Required]
        [MaxLength(100)]
        public string Code { get; set; } = string.Empty;
    }

    private sealed record LoginSecondFactorChallenge(Guid UserId, string Username, string ReturnUrl);
}
