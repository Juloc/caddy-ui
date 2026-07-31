using CaddyUi.Infrastructure.Cutover;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class CutoverModel : LocalizedPageModel
{
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(30);
    private readonly CutoverReadinessService _service;
    private readonly CutoverOptions _options;

    public CutoverModel(CutoverReadinessService service, CutoverOptions options)
    {
        _service = service;
        _options = options;
    }

    public CutoverReadinessReport? Report { get; private set; }

    public CutoverComparisonReport? Comparison { get; private set; }

    public CutoverOptions Options => _options;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostCaptureAsync()
    {
        using var timeout = CreateTimeout();
        try
        {
            Report = await _service.CaptureAsync(timeout.Token);
            var path = await _service.WriteReadinessManifestAsync(Report, timeout.Token);
            TempData[Report.IsReady ? "Message" : "Error"] = Report.IsReady
                ? $"Readiness-Manifest geschrieben: {path}"
                : $"Manifest mit {Report.BlockedCount} Blockern geschrieben: {path}";
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Error"] = "Die Readiness-Prüfung wurde nach 30 Sekunden beendet. Prüfe PostgreSQL, Backup-Pfade und die Legacy-Datei einzeln.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCompareAsync()
    {
        using var timeout = CreateTimeout();
        try
        {
            Comparison = await _service.CompareConfiguredSnapshotAsync(timeout.Token);
            var path = await _service.WriteComparisonManifestAsync(Comparison, timeout.Token);
            TempData[Comparison.IsWithinTolerance ? "Message" : "Error"] = Comparison.IsWithinTolerance
                ? $"Statistikvergleich bestanden: {path}"
                : $"Statistikvergleich außerhalb der Toleranz: {path}";
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Error"] = "Der Statistikvergleich wurde nach 30 Sekunden beendet.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return Page();
    }

    private CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(InteractiveTimeout);
        return timeout;
    }
}
