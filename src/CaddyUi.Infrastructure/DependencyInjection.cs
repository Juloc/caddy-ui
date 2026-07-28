using CaddyUi.Application;
using CaddyUi.Application.Analytics;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Security;
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
        var analyticsOptions = AnalyticsIngestionOptions.FromConfiguration(configuration);

        services.AddSingleton<FoundationStatusService>();
        services.AddDbContext<CaddyUiDbContext>(options => Configure(options, connectionString));
        services.AddSingleton<IDbContextFactory<CaddyUiDbContext>>(
            new RuntimeDbContextFactory(connectionString));
        services.AddSingleton<AuthenticationStore>();
        services.AddSingleton<LoginProtectionService>();
        services.AddSingleton<DomainProviderStore>();
        services.AddSingleton(analyticsOptions);
        services.AddSingleton<CaddyAccessLogParser>();
        services.AddSingleton<RequestClassifier>();
        services.AddSingleton<AnalyticsLogTailer>();
        services.AddSingleton<AnalyticsIngestionStore>();
        services.AddSingleton<AnalyticsClientKeyProvider>();
        services.AddHostedService<AnalyticsIngestionWorker>();
        services.AddHostedService<AnalyticsMaintenanceWorker>();

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("CaddyUi");
        if (configuration.GetValue("DataProtection:PersistKeysToPostgreSql", true))
        {
            dataProtection.PersistKeysToDbContext<CaddyUiDbContext>();
        }

        return services;
    }

    private static void Configure(
        DbContextOptionsBuilder options,
        string connectionString)
    {
        options.UseNpgsql(
            connectionString,
            postgres =>
            {
                postgres.MigrationsAssembly(typeof(CaddyUiDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__EFMigrationsHistory", "public");
            });
    }
}
