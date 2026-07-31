using System.ComponentModel.DataAnnotations;
using CaddyUi.Application.Routing;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Routing;

[Authorize(Policy = "Editor")]
public sealed class PreviewModel : LocalizedPageModel
{
    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;

    public PreviewModel(RouteManagementStore store, CaddyApplyService applyService)
    {
        _store = store;
        _applyService = applyService;
    }

    [BindProperty]
    public PreviewInput Input { get; set; } = new();

    public RoutePreviewResult? Preview { get; private set; }

    public IReadOnlyList<RouteRevisionRecord> Revisions { get; private set; } =
        Array.Empty<RouteRevisionRecord>();

    public IReadOnlyList<ApplyOperationRecord> Operations { get; private set; } =
        Array.Empty<ApplyOperationRecord>();

    public RoutingOptions RoutingOptions => _applyService.Options;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(Guid? revision)
    {
        await LoadHistoryAsync();
        if (revision is Guid revisionId)
        {
            var selected = await _store.GetRevisionAsync(revisionId, HttpContext.RequestAborted);
            if (selected is not null)
            {
                var current = await _applyService.ReadCurrentContentAsync(HttpContext.RequestAborted);
                Preview = new RoutePreviewResult(
                    selected,
                    current,
                    LineDiff.Create(current, selected.Content),
                    Array.Empty<string>(),
                    RoutingOptions.WriteMode,
                    RoutingOptions.WriteMode == RouteWriteMode.Active
                        ? RoutingOptions.ManagedFragmentPath
                        : RoutingOptions.ShadowFragmentPath);
            }
        }
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadHistoryAsync();
            return Page();
        }

        try
        {
            Preview = await _applyService.CreatePreviewAsync(
                Input.Reason,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            await LoadHistoryAsync();
            return Page();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadHistoryAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApplyAsync(Guid revisionId)
    {
        try
        {
            var result = await _applyService.ApplyAsync(
                revisionId,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = result.Message;
            return RedirectToPage(new { revision = revisionId });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadHistoryAsync();
            var selected = await _store.GetRevisionAsync(revisionId, HttpContext.RequestAborted);
            if (selected is not null)
            {
                var current = await _applyService.ReadCurrentContentAsync(HttpContext.RequestAborted);
                Preview = new RoutePreviewResult(
                    selected,
                    current,
                    LineDiff.Create(current, selected.Content),
                    Array.Empty<string>(),
                    RoutingOptions.WriteMode,
                    RoutingOptions.WriteMode == RouteWriteMode.Active
                        ? RoutingOptions.ManagedFragmentPath
                        : RoutingOptions.ShadowFragmentPath);
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnPostRollbackAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.Reason))
        {
            ModelState.AddModelError("Input.Reason", "Für ein Rollback ist ein konkreter Grund erforderlich.");
            await LoadHistoryAsync();
            return Page();
        }

        try
        {
            var result = await _applyService.RollbackLastAsync(
                Input.Reason,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = result.Message;
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadHistoryAsync();
            return Page();
        }
    }

    private async Task LoadHistoryAsync()
    {
        Revisions = await _store.ListRevisionsAsync(40, HttpContext.RequestAborted);
        Operations = await _store.ListOperationsAsync(40, HttpContext.RequestAborted);
    }

    public sealed class PreviewInput
    {
        [Required]
        [MaxLength(500)]
        public string Reason { get; set; } = "Routing-Konfiguration aktualisieren";
    }
}
