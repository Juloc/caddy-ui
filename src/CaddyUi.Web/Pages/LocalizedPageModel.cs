using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages;

/// <summary>
/// Provides the shared UI localizer to Razor Page models without duplicating a
/// localization dependency in every page constructor. The localizer remains
/// request-scoped and uses the culture selected by <see cref="Localization.UserCultureMiddleware"/>.
/// </summary>
public abstract class LocalizedPageModel : PageModel
{
    protected string Text(string key)
    {
        return HttpContext.RequestServices
            .GetRequiredService<IStringLocalizer<SharedResource>>()[key];
    }

    protected string Text(string key, params object[] arguments)
    {
        return HttpContext.RequestServices
            .GetRequiredService<IStringLocalizer<SharedResource>>()[key, arguments];
    }
}
