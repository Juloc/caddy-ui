using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.Performance;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    public IndexModel(AnalyticsReadStore store, TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public PerformanceAnalyticsSnapshot Performance { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Performance = await Store.GetPerformanceAsync(filter, HttpContext.RequestAborted);
    }
}
