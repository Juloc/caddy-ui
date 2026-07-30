using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    public IndexModel(
        AnalyticsReadStore store,
        TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public AnalyticsDashboardSnapshot Dashboard { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Dashboard = await Store.GetDashboardAsync(filter, HttpContext.RequestAborted);
    }
}
