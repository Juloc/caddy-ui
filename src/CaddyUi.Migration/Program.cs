using CaddyUi.Infrastructure;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
    options.UseUtcTimestamp = true;
});

builder.Services.AddCaddyUiInfrastructure(builder.Configuration);

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();

var logger = scope.ServiceProvider
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("CaddyUi.Migration");
var database = scope.ServiceProvider.GetRequiredService<CaddyUiDbContext>();

logger.LogInformation("Applying Caddy UI PostgreSQL migrations.");
await database.Database.MigrateAsync();
logger.LogInformation("Caddy UI PostgreSQL migrations completed.");
