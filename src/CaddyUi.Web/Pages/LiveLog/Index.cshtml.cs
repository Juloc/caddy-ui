using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.LiveLog;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    private readonly bool _ingestionEnabled;

    public IndexModel(AnalyticsReadStore store, TimeProvider timeProvider, AnalyticsIngestionOptions options)
        : base(store, timeProvider)
    {
        _ingestionEnabled = options.Enabled;
    }

    public string? EventStreamUrl { get; private set; }

    public string? LoadError { get; private set; }

    public override bool IsLiveView => true;

    public async Task OnGetAsync()
    {
        if (!_ingestionEnabled)
        {
            LoadError = Text("Analytics ingestion is disabled.");
            return;
        }
        try
        {
            Limit = Math.Clamp(Limit, 1, 100);
            await PrepareFilterAsync(HttpContext.RequestAborted);
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
            EventStreamUrl = null;
            LoadError = Text("Live log is currently unavailable");
        }
    }
}
