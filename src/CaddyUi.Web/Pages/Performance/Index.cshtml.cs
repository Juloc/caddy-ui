using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;

namespace CaddyUi.Web.Pages.Performance;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : AnalyticsPageModelBase
{
    private readonly AnalyticsIngestionOptions _ingestionOptions;
    private readonly AnalyticsIngestionRuntimeMetrics _runtimeMetrics;

    public IndexModel(
        AnalyticsReadStore store,
        TimeProvider timeProvider,
        AnalyticsIngestionOptions ingestionOptions,
        AnalyticsIngestionRuntimeMetrics runtimeMetrics)
        : base(store, timeProvider)
    {
        _ingestionOptions = ingestionOptions;
        _runtimeMetrics = runtimeMetrics;
    }

    public PerformanceAnalyticsSnapshot Performance { get; private set; } = null!;

    public bool IngestionEnabled => _ingestionOptions.Enabled;

    public int IngestionBatchSize => _ingestionOptions.BatchSize;

    public int IngestionBacklogDelayMilliseconds => _ingestionOptions.BacklogDelayMilliseconds;

    public AnalyticsIngestionRuntimeSnapshot? Ingestion { get; private set; }

    public async Task OnGetAsync()
    {
        var filter = await PrepareFilterAsync(HttpContext.RequestAborted);
        Performance = await Store.GetPerformanceAsync(filter, HttpContext.RequestAborted);
        Ingestion = _runtimeMetrics.GetLatest();
    }
}
