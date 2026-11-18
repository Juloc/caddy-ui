using System.Security.Claims;
using CaddyUi.Web.Pages.SystemStatus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Tests;

public sealed class AnalyticsRetentionAuthorizationTests
{
    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    public async Task RetentionPost_RequiresAdministratorBeforeDatabaseAccess(string role)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, "test"))
        };
        var model = new IndexModel(null!, null!, new CaddyUi.Infrastructure.Analytics.AnalyticsIngestionRuntimeMetrics()) { PageContext = new PageContext { HttpContext = context } };
        Assert.IsType<ForbidResult>(await model.OnPostRetentionAsync());
    }
}
