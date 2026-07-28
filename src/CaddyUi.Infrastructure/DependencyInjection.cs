using CaddyUi.Application;
using CaddyUi.Application.Analytics;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
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
        var ipSecurityOptions = IpSecurityOptions.FromConfiguration(configuration);
        var routingOptions = RoutingOptions.FromConfiguration(configuration);

        services.AddSingleton<FoundationStatusService>();
        services.AddDbContext<CaddyUiDbContext>(options => Configure(options, connectionString));
        services.AddSingleton<IDbContextFactory<CaddyUiDbContext>>(
            new RuntimeDbContextFactory(connectionString));
        services.AddSingleton<AuthenticationStore>();
        services.AddSingleton<LoginProtectionService>();
        services.AddSingleton<DomainProviderStore>();
        services.AddSingleton<RouteManagementStore>();
        services.AddSingleton<RouteImportStore>();
        services.AddSingleton<RouteTransferService>();
        services.AddSingleton<AccessGroupStateStore>();
        services.AddSingleton(routingOptions);
        services.AddSingleton<ICaddyCommandRunner, ProcessCaddyCommandRunner>();
        services.AddSingleton<CaddyApplyService>();
        services.AddSingleton(analyticsOptions);
        services.AddSingleton<CaddyAccessLogParser>();
        services.AddSingleton<RequestClassifier>();
        services.AddSingleton<AnalyticsLogTailer>();
        services.AddSingleton<AnalyticsIngestionStore>();
        services.AddSingleton<AnalyticsClientKeyProvider>();
        services.AddSingleton<AnalyticsReadStore>();
        services.AddHostedService<AnalyticsIngestionWorker>();
        services.AddHostedService<AnalyticsMaintenanceWorker>();
        services.AddSingleton(ipSecurityOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IpAddressClassifier>();
        services.AddSingleton<ClientRiskEngine>();
        services.AddSingleton<IpIntelligenceStore>();
        services.AddSingleton<ClientRiskAssessmentStore>();
        services.AddSingleton<ClientSecurityQueryStore>();
        services.AddSingleton<AtomicBlocklistWriter>();
        services.AddSingleton<IpBlockService>();
        services.AddHttpClient<IIpIntelligenceProvider, RipeStatIpIntelligenceProvider>(client =>
        {
            client.BaseAddress = ipSecurityOptions.RipeStatBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(ipSecurityOptions.ProviderTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caddy-UI/2.0");
        });
        services.AddHostedService<IpIntelligenceRefreshWorker>();
        services.AddHostedService<ClientRiskAssessmentWorker>();

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
