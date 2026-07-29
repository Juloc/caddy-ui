using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.Traffic;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    public IndexModel(AnalyticsReadStore store, TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public IReadOnlyList<TrafficSeriesPoint> Series { get; private set; } =
        Array.Empty<TrafficSeriesPoint>();

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Series = await Store.GetTrafficAsync(filter, HttpContext.RequestAborted);
    }
}
