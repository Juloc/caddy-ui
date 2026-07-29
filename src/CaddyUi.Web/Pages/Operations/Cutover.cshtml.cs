using CaddyUi.Infrastructure.Cutover;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class CutoverModel : PageModel
{
    private readonly CutoverReadinessService _service;
    private readonly CutoverOptions _options;

    public CutoverModel(CutoverReadinessService service, CutoverOptions options)
    {
        _service = service;
        _options = options;
    }

    public CutoverReadinessReport Report { get; private set; } = null!;

    public CutoverComparisonReport? Comparison { get; private set; }

    public CutoverOptions Options => _options;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCaptureAsync()
    {
        var report = await _service.CaptureAsync(HttpContext.RequestAborted);
        var path = await _service.WriteReadinessManifestAsync(report, HttpContext.RequestAborted);
        TempData[report.IsReady ? "Message" : "Error"] = report.IsReady
            ? $"Readiness-Manifest geschrieben: {path}"
            : $"Manifest mit {report.BlockedCount} Blockern geschrieben: {path}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompareAsync()
    {
        try
        {
            var report = await _service.CompareConfiguredSnapshotAsync(HttpContext.RequestAborted);
            var path = await _service.WriteComparisonManifestAsync(report, HttpContext.RequestAborted);
            TempData[report.IsWithinTolerance ? "Message" : "Error"] = report.IsWithinTolerance
                ? $"Statistikvergleich bestanden: {path}"
                : $"Statistikvergleich außerhalb der Toleranz: {path}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Report = await _service.CaptureAsync(HttpContext.RequestAborted);
        if (System.IO.File.Exists(_options.LegacyStatisticsPath))
        {
            try
            {
                Comparison = await _service.CompareConfiguredSnapshotAsync(HttpContext.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                TempData["Error"] = exception.Message;
            }
        }
    }
}
