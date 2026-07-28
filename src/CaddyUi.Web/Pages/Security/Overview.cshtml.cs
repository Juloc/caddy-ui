using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.Security;

[Authorize(Policy = "Viewer")]
public sealed class OverviewModel : AnalyticsPageModelBase
{
    public OverviewModel(AnalyticsReadStore store, TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public SecurityAnalyticsSnapshot Security { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Security = await Store.GetSecurityAsync(filter, HttpContext.RequestAborted);
    }
}
