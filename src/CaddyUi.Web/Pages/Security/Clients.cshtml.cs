using CaddyUi.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Security;

[Authorize(Policy = "Viewer")]
public sealed class ClientsModel : LocalizedPageModel
{
    private readonly ClientSecurityQueryStore _store;

    public ClientsModel(ClientSecurityQueryStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ClientSecuritySummary> Clients { get; private set; } =
        Array.Empty<ClientSecuritySummary>();

    public async Task OnGetAsync()
    {
        Clients = await _store.ListAsync(cancellationToken: HttpContext.RequestAborted);
    }
}
