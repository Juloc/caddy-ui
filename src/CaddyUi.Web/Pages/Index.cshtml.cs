using CaddyUi.Application;
using CaddyUi.Contracts;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages;

public sealed class IndexModel : PageModel
{
    private readonly FoundationStatusService _foundationStatus;

    public IndexModel(FoundationStatusService foundationStatus)
    {
        _foundationStatus = foundationStatus;
    }

    public FoundationStatus Status { get; private set; } = null!;

    public void OnGet()
    {
        Status = _foundationStatus.GetStatus();
    }
}
