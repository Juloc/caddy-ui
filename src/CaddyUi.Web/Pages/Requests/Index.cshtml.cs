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

    public string? LoadError { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
            Requests = await Store.GetRequestsAsync(
                filter,
                InitialCursor(filter),
                HttpContext.RequestAborted);
            LoadError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Requests = Array.Empty<RequestAnalyticsRow>();
            LoadError = $"Requests konnten nicht geladen werden: {exception.Message}";
        }
    }

    private static DateTimeOffset InitialCursor(AnalyticsReadFilter filter)
    {
        return filter.From == DateTimeOffset.MinValue
            ? filter.From
            : filter.From.AddTicks(-1);
    }
}
