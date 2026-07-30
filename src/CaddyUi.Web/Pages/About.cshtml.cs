using CaddyUi.Application;
using CaddyUi.Contracts;
using CaddyUi.Infrastructure.Cutover;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages;

[Authorize(Policy = "Viewer")]
public sealed class AboutModel : PageModel
{
    private readonly FoundationStatusService _foundationStatusService;
    private readonly IHostEnvironment _hostEnvironment;

    public AboutModel(
        FoundationStatusService foundationStatusService,
        IHostEnvironment hostEnvironment,
        RoutingOptions routingOptions,
        OperationsOptions operationsOptions,
        CutoverOptions cutoverOptions)
    {
        _foundationStatusService = foundationStatusService;
        _hostEnvironment = hostEnvironment;
        Routing = routingOptions;
        Operations = operationsOptions;
        Cutover = cutoverOptions;
    }

    public FoundationStatus Product { get; private set; } = null!;

    public string EnvironmentName => _hostEnvironment.EnvironmentName;

    public RoutingOptions Routing { get; }

    public OperationsOptions Operations { get; }

    public CutoverOptions Cutover { get; }

    public void OnGet()
    {
        Product = _foundationStatusService.GetStatus();
    }
}
