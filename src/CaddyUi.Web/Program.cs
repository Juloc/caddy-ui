using System.Net.Mime;
using System.Text.Json;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Localization;
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
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddSingleton<UiCultureCatalog>();
builder.Services
    .AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();
builder.Services.AddCaddyUiInfrastructure(builder.Configuration);
builder.Services.AddSingleton<PasswordHashService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddCaddyUiAuthentication(securityOptions);
builder.Services.AddHostedService<AuthenticationBootstrapService>();
builder.Services.AddHostedService<ManagedRouteReconciliationWorker>();
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
app.UseMiddleware<UserCultureMiddleware>();
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

app.MapGet(
        "/events/live",
        async (
            HttpContext context,
            AnalyticsReadStore store,
            TimeProvider timeProvider) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Append("X-Accel-Buffering", "no");

            var initialFilter = CreateLiveFilter(context, timeProvider.GetUtcNow());
            var after = timeProvider.GetUtcNow();
            var once = bool.TryParse(context.Request.Query["once"], out var parsedOnce) && parsedOnce;

            try
            {
                do
                {
                    var filter = initialFilter with { To = timeProvider.GetUtcNow() };
                    var requests = await store.GetRequestsAsync(
                        filter,
                        after,
                        context.RequestAborted);
                    foreach (var request in requests.OrderBy(item => item.OccurredAt))
                    {
                        var payload = JsonSerializer.Serialize(
                            new
                            {
                                occurredAt = request.OccurredAt,
                                request.Host,
                                request.Method,
                                request.Path,
                                request.Status,
                                request.RequestType,
                                request.ActorType,
                                request.DurationMilliseconds,
                                request.BytesSent,
                                request.RemoteAddress,
                                request.ClientId,
                            });
                        await context.Response.WriteAsync(
                            $"event: request\ndata: {payload}\n\n",
                            context.RequestAborted);
                    }

                    if (requests.Count > 0)
                    {
                        after = requests.Max(item => item.OccurredAt);
                    }

                    await context.Response.WriteAsync(": keepalive\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    if (!once)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
                    }
                }
                while (!once && !context.RequestAborted.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        })
    .RequireAuthorization("Viewer");

app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapRazorPages();
app.Run();

static AnalyticsReadFilter CreateLiveFilter(HttpContext context, DateTimeOffset now)
{
    var range = context.Request.Query["range"].ToString().Trim().ToLowerInvariant();
    var from = range switch
    {
        "1h" => now.AddHours(-1),
        "7d" => now.AddDays(-7),
        "30d" => now.AddDays(-30),
        "90d" => now.AddDays(-90),
        _ => now.AddHours(-24),
    };
    var limit = int.TryParse(
        context.Request.Query["limit"],
        System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture,
        out var parsedLimit)
            ? parsedLimit
            : 100;
    return AnalyticsReadFilter.Create(
        from,
        now,
        context.Request.Query["host"],
        context.Request.Query["actor"],
        context.Request.Query["type"],
        context.Request.Query["status"],
        limit);
}

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
