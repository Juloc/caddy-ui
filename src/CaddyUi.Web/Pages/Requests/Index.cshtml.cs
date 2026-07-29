using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.Requests;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    public IndexModel(AnalyticsReadStore store, TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public IReadOnlyList<RequestAnalyticsRow> Requests { get; private set; } =
        Array.Empty<RequestAnalyticsRow>();

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Requests = await Store.GetRequestsAsync(filter, cancellationToken: HttpContext.RequestAborted);
    }
}
