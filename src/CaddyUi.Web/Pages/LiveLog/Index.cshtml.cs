using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.LiveLog;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    public IndexModel(AnalyticsReadStore store, TimeProvider timeProvider)
        : base(store, timeProvider)
    {
    }

    public IReadOnlyList<RequestAnalyticsRow> Requests { get; private set; } =
        Array.Empty<RequestAnalyticsRow>();

    public string EventStreamUrl { get; private set; } = "/events/live";

    public async Task OnGetAsync()
    {
        Limit = Math.Clamp(Limit, 1, 100);
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Requests = await Store.GetRequestsAsync(filter, cancellationToken: HttpContext.RequestAborted);
        EventStreamUrl = QueryString.Create(
            new Dictionary<string, string?>
            {
                ["range"] = Range,
                ["host"] = Host,
                ["actor"] = Actor,
                ["type"] = Type,
                ["status"] = Status,
                ["limit"] = "100",
            }).Value ?? "/events/live";
        EventStreamUrl = "/events/live" + EventStreamUrl;
    }
}
