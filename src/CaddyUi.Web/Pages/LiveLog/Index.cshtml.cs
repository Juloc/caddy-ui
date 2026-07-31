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

    public string? EventStreamUrl { get; private set; }

    public string? LoadError { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            Limit = Math.Clamp(Limit, 1, 100);
            var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
            Requests = await Store.GetRequestsAsync(filter, cancellationToken: HttpContext.RequestAborted);
            var query = QueryString.Create(
                new Dictionary<string, string?>
                {
                    ["range"] = Range,
                    ["host"] = Host,
                    ["actor"] = Actor,
                    ["type"] = Type,
                    ["status"] = Status,
                    ["limit"] = "100",
                }).Value;
            EventStreamUrl = "/events/live" + query;
            LoadError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Requests = Array.Empty<RequestAnalyticsRow>();
            EventStreamUrl = null;
            LoadError = $"Live-Log konnte nicht gestartet werden: {exception.Message}";
        }
    }
}
