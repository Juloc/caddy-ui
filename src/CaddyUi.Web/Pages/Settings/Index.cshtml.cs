using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Settings;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : PageModel
{
    private readonly UserPreferenceStore _preferences;
    private readonly UiCultureCatalog _cultures;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public IndexModel(
        UserPreferenceStore preferences,
        UiCultureCatalog cultures,
        IStringLocalizer<SharedResource> localizer)
    {
        _preferences = preferences;
        _cultures = cultures;
        _localizer = localizer;
    }

    [BindProperty]
    public PreferenceInput Input { get; set; } = new();

    public IReadOnlyList<UiCultureOption> Cultures => _cultures.SupportedCultures;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var userId = UserId();
        Input.Language = _cultures.Normalize(
            await _preferences.GetLanguageAsync(userId, HttpContext.RequestAborted));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!_cultures.TryNormalize(Input.Language, out var language))
        {
            ModelState.AddModelError(
                nameof(Input.Language),
                _localizer["The selected language is not supported."]);
            return Page();
        }

        await _preferences.SetLanguageAsync(
            UserId(),
            language,
            HttpContext.RequestAborted);
        Response.Cookies.Append(
            UiCultureCatalog.LanguageCookieName,
            language,
            UserCultureMiddleware.CreateCookieOptions(HttpContext));
        StatusMessage = _localizer["Language preference saved."];
        return RedirectToPage();
    }

    private Guid UserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated user has no valid identifier.");
    }

    public sealed class PreferenceInput
    {
        [Required]
        [MaxLength(16)]
        public string Language { get; set; } = UiCultureCatalog.FallbackCulture;
    }
}
