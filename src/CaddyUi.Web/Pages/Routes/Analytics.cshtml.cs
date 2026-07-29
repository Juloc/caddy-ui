using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.Routes;

[Authorize(Policy = "Viewer")]
public sealed class AnalyticsModel : AnalyticsPageModelBase
{
    public AnalyticsModel(AnalyticsReadStore store, TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public IReadOnlyList<RouteAnalyticsRow> Routes { get; private set; } =
        Array.Empty<RouteAnalyticsRow>();

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Routes = await Store.GetRouteAnalyticsAsync(filter, HttpContext.RequestAborted);
    }
}
