using System.ComponentModel.DataAnnotations;
using System.Text;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Routing;

[Authorize(Policy = "Editor")]
public sealed class TransferModel : PageModel
{
    private readonly RouteTransferService _transferService;
    private readonly RouteManagementStore _routeStore;

    public TransferModel(
        RouteTransferService transferService,
        RouteManagementStore routeStore)
    {
        _transferService = transferService;
        _routeStore = routeStore;
    }

    [BindProperty]
    public ImportInput Input { get; set; } = new();

    public int RouteCount { get; private set; }

    public string ExportPreview { get; private set; } = string.Empty;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnGetDownloadAsync()
    {
        var json = await _transferService.ExportAsync(HttpContext.RequestAborted);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return File(
            Encoding.UTF8.GetBytes(json),
            "application/json; charset=utf-8",
            $"caddy-ui-routes-{timestamp}.json");
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            var result = await _transferService.ImportAsync(
                Input.Json,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = $"{result.ImportedRoutes} Route(n) atomar importiert. Caddy bleibt unverändert, bis eine Preview erzeugt und angewendet wird.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    private async Task LoadAsync()
    {
        var routes = await _routeStore.ListRoutesAsync(HttpContext.RequestAborted);
        RouteCount = routes.Count;
        ExportPreview = await _transferService.ExportAsync(HttpContext.RequestAborted);
    }

    public sealed class ImportInput
    {
        [Required]
        [MaxLength(2_000_000)]
        public string Json { get; set; } = string.Empty;
    }
}
