using CaddyUi.Application;
using CaddyUi.Contracts;
using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    private readonly FoundationStatusService _foundationStatus;

    public IndexModel(
        FoundationStatusService foundationStatus,
        AnalyticsReadStore store,
        TimeProvider timeProvider)
        : base(store, timeProvider)
    {
        _foundationStatus = foundationStatus;
    }

    public FoundationStatus StatusInfo { get; private set; } = null!;

    public AnalyticsDashboardSnapshot Dashboard { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        StatusInfo = _foundationStatus.GetStatus();
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Dashboard = await Store.GetDashboardAsync(filter, HttpContext.RequestAborted);
    }
}
