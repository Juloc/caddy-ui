using System.ComponentModel.DataAnnotations;
using System.Net;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Security;

[Authorize(Policy = "Viewer")]
public sealed class ClientModel : PageModel
{
    private readonly ClientSecurityQueryStore _store;
    private readonly IpBlockService _blockService;

    public ClientModel(
        ClientSecurityQueryStore store,
        IpBlockService blockService)
    {
        _store = store;
        _blockService = blockService;
    }

    public ClientSecurityDetails? Details { get; private set; }

    [BindProperty]
    [Required]
    [StringLength(500, MinimumLength = 3)]
    public string BlockReason { get; set; } = "Auffälliger Client";

    [BindProperty]
    [Range(1, 720)]
    public int BlockHours { get; set; } = 24;

    [BindProperty(SupportsGet = true)]
    public string StatusMessage { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Details = await _store.GetAsync(id, HttpContext.RequestAborted);
        return Details is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostBlockAsync(Guid id)
    {
        if (!CanEditSecurity())
        {
            return Forbid();
        }

        Details = await _store.GetAsync(id, HttpContext.RequestAborted);
        if (Details is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid ||
            !IPAddress.TryParse(Details.Summary.LatestAddress, out var address))
        {
            return Page();
        }

        var result = await _blockService.BlockAsync(
            address.ToString(),
            BlockReason,
            DateTimeOffset.UtcNow.AddHours(BlockHours),
            User.RequireUserId(),
            User.Identity?.Name ?? "unknown",
            HttpContext.Connection.RemoteIpAddress,
            HttpContext.RequestAborted);
        return RedirectToPage(
            new
            {
                id,
                statusMessage = $"Sperre gespeichert: {result.ActivationState} bis {result.ExpiresAt?.ToLocalTime():g}.",
            });
    }

    public async Task<IActionResult> OnPostUnblockAsync(
        Guid id,
        Guid ruleId,
        string unblockReason)
    {
        if (!CanEditSecurity())
        {
            return Forbid();
        }

        var result = await _blockService.UnblockAsync(
            ruleId,
            unblockReason,
            User.RequireUserId(),
            User.Identity?.Name ?? "unknown",
            HttpContext.Connection.RemoteIpAddress,
            HttpContext.RequestAborted);
        return RedirectToPage(
            new
            {
                id,
                statusMessage = $"Sperre {result.Target} wurde aufgehoben.",
            });
    }

    private bool CanEditSecurity()
    {
        return User.IsInRole("admin") || User.IsInRole("editor");
    }
}
