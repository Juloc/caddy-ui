using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.SystemStatus;

[Authorize(Policy = "Viewer")]
public sealed class IndexModel : LocalizedPageModel
{
    private readonly AnalyticsReadStore _store;

    public IndexModel(AnalyticsReadStore store)
    {
        _store = store;
    }

    public SystemAnalyticsSnapshot Status { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Status = await _store.GetSystemAsync(HttpContext.RequestAborted);
    }
}
