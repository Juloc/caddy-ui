using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class HealthModel : LocalizedPageModel
{
    private readonly OperationsStore _store;
    private readonly HealthProbeService _health;

    public HealthModel(OperationsStore store, HealthProbeService health)
    {
        _store = store;
        _health = health;
    }

    public IReadOnlyList<HealthTargetRecord> Targets { get; private set; } = Array.Empty<HealthTargetRecord>();

    [BindProperty]
    public HealthInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Targets = await _store.ListHealthTargetsAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            Targets = await _store.ListHealthTargetsAsync(HttpContext.RequestAborted);
            return Page();
        }

        try
        {
            await _store.CreateHealthTargetAsync(
                Input.Name,
                Input.TargetType,
                Input.Url,
                Input.ExpectedStatusMin,
                Input.ExpectedStatusMax,
                Input.TimeoutSeconds,
                HttpContext.RequestAborted);
            TempData["Message"] = "Health-Ziel wurde angelegt.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Targets = await _store.ListHealthTargetsAsync(HttpContext.RequestAborted);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid targetId, bool enabled)
    {
        await _store.SetHealthTargetEnabledAsync(targetId, enabled, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunAsync(Guid targetId)
    {
        var target = (await _store.ListHealthTargetsAsync(HttpContext.RequestAborted))
            .FirstOrDefault(item => item.Id == targetId) ??
            throw new InvalidOperationException("Das Health-Ziel existiert nicht mehr.");
        var result = await _health.CheckAsync(target, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public sealed class HealthInput
    {
        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string TargetType { get; set; } = "public";

        [Required]
        [Url]
        public string Url { get; set; } = string.Empty;

        [Range(100, 599)]
        public int ExpectedStatusMin { get; set; } = 200;

        [Range(100, 599)]
        public int ExpectedStatusMax { get; set; } = 399;

        [Range(1, 120)]
        public int TimeoutSeconds { get; set; } = 5;
    }
}
