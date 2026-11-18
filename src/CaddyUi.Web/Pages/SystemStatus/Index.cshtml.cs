using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.SystemStatus;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : LocalizedPageModel
{
    private readonly AnalyticsReadStore _store;
    private readonly AnalyticsIngestionStore _ingestion;
    private readonly AnalyticsIngestionRuntimeMetrics _metrics;

    public IndexModel(AnalyticsReadStore store, AnalyticsIngestionStore ingestion, AnalyticsIngestionRuntimeMetrics metrics)
    {
        _store = store;
        _ingestion = ingestion;
        _metrics = metrics;
    }

    public SystemAnalyticsSnapshot Status { get; private set; } = null!;
    public AnalyticsIngestionRuntimeSnapshot? Ingestion { get; private set; }

    [BindProperty, Range(1, 3650)]
    public int StatisticsDays { get; set; } = 90;

    [BindProperty, Range(1, 3650)]
    public int SecurityEventDays { get; set; } = 30;

    public async Task OnGetAsync()
    {
        Status = await _store.GetSystemAsync(HttpContext.RequestAborted);
        Ingestion = _metrics.GetLatest();
        StatisticsDays = Status.StatisticsRetentionDays;
        SecurityEventDays = Status.SecurityEventRetentionDays;
    }

    public async Task<IActionResult> OnPostRetentionAsync()
    {
        if (!User.IsInRole("admin"))
            return Forbid();
        Status = await _store.GetSystemAsync(HttpContext.RequestAborted);
        Ingestion = _metrics.GetLatest();
        if (!ModelState.IsValid)
            return Page();
        try
        {
            await _ingestion.SaveRetentionAsync(StatisticsDays, SecurityEventDays, HttpContext.RequestAborted);
            return RedirectToPage();
        }
        catch (DbException)
        {
            ModelState.AddModelError(string.Empty, Text("Could not save retention settings."));
            return Page();
        }
    }
}
