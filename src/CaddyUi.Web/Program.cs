using System.Net.Mime;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
    options.UseUtcTimestamp = true;
});

var securityOptions = SecurityRuntimeOptions.FromConfiguration(builder.Configuration);
builder.Services.AddRazorPages();
builder.Services.AddCaddyUiInfrastructure(builder.Configuration);
builder.Services.AddSingleton<PasswordHashService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddCaddyUiAuthentication(securityOptions);
builder.Services.AddHostedService<AuthenticationBootstrapService>();
builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: new[] { "live", "ready" })
    .AddDbContextCheck<CaddyUiDbContext>(
        "postgresql",
        tags: new[] { "ready" });

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<CaddyUiDbContext>();
    await database.Database.MigrateAsync();
}

if (securityOptions.PublicAccessWithoutMandatoryTotp)
{
    app.Logger.LogWarning(
        "Public Caddy UI access is configured without mandatory TOTP. CADDY_UI_REQUIRE_TOTP=false remains supported, but the UI will display a warning.");
}

app.UseMiddleware<RequestSurfaceMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<OriginValidationMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live", StringComparer.Ordinal),
        ResponseWriter = WriteHealthResponseAsync,
    });

app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready", StringComparer.Ordinal),
        ResponseWriter = WriteHealthResponseAsync,
    });

app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapRazorPages();
app.Run();

static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = MediaTypeNames.Application.Json;

    return context.Response.WriteAsJsonAsync(
        new
        {
            status = report.Status.ToString(),
            checks = report.Entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    durationMilliseconds = entry.Value.Duration.TotalMilliseconds,
                }),
        });
}

public partial class Program
{
}
