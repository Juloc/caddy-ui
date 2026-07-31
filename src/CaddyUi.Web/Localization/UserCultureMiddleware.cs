using System.Globalization;
using System.Security.Claims;

namespace CaddyUi.Web.Localization;

/// <summary>
/// Applies the authenticated user's language preference before Razor renders a page.
/// The plain culture cookie also covers login and portal pages before authentication.
/// </summary>
public sealed class UserCultureMiddleware
{
    private readonly RequestDelegate _next;

    public UserCultureMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, UiCultureCatalog catalog)
    {
        var requested = context.Request.Cookies[UiCultureCatalog.LanguageCookieName];
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = context.User.FindFirstValue(UiCultureCatalog.LanguageClaimType);
        }

        var cultureName = catalog.TryNormalize(requested, out var normalized)
            ? normalized
            : catalog.DefaultCulture;
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        context.Response.Headers.ContentLanguage = cultureName;

        await _next(context);
    }

    public static CookieOptions CreateCookieOptions(HttpContext context)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
        };
    }
}
