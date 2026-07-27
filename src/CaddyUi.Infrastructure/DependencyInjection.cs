using CaddyUi.Application;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyUi.Infrastructure;

public static class DependencyInjection
{
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=caddy_ui;Username=caddy_ui;Password=caddy_ui";

    public static IServiceCollection AddCaddyUiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString =
            configuration.GetConnectionString("CaddyUi") ??
            DefaultConnectionString;

        services.AddSingleton<FoundationStatusService>();
        services.AddDbContext<CaddyUiDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                postgres =>
                {
                    postgres.MigrationsAssembly(typeof(CaddyUiDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__EFMigrationsHistory", "public");
                }));
        services.AddDataProtection()
            .SetApplicationName("CaddyUi")
            .PersistKeysToDbContext<CaddyUiDbContext>();

        return services;
    }
}
