using CaddyUi.Application;
using CaddyUi.Application.Analytics;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Certificates;
using CaddyUi.Infrastructure.Cutover;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Infrastructure.Security;
using CaddyUi.Infrastructure.Setup;
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
        var operationsOptions = OperationsOptions.FromConfiguration(configuration);
        var cutoverOptions = CutoverOptions.FromConfiguration(configuration);

        services.AddSingleton<FoundationStatusService>();
        services.AddDbContext<CaddyUiDbContext>(options => Configure(options, connectionString));
        services.AddSingleton<IDbContextFactory<CaddyUiDbContext>>(
            new RuntimeDbContextFactory(connectionString));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("CaddyUi");
        if (configuration.GetValue("DataProtection:PersistKeysToPostgreSql", true))
        {
            dataProtection.PersistKeysToDbContext<CaddyUiDbContext>();
        }

        services.AddSingleton<AuthenticationStore>();
        services.AddSingleton<LoginProtectionService>();
        services.AddSingleton<DomainProviderStore>();
        services.AddSingleton<CertificateStatusService>();
        services.AddSingleton<RouteManagementStore>();
        services.AddSingleton<RouteImportStore>();
        services.AddSingleton<RouteTransferService>();
        services.AddSingleton<AccessGroupStateStore>();
        services.AddSingleton(routingOptions);
        services.AddSingleton<GuidedSetupService>();
        services.AddSingleton<ICaddyCommandRunner, ProcessCaddyCommandRunner>();

        services.AddSingleton(operationsOptions);
        services.AddSingleton<OperationsStore>();
        services.AddSingleton<SecretReferenceResolver>();
        services.AddSingleton<ISecretReferenceResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<SecretReferenceResolver>());
        services.AddSingleton<ISecretReferenceProtector>(serviceProvider =>
            serviceProvider.GetRequiredService<SecretReferenceResolver>());
        services.AddSingleton<IDnsProviderAdapter, NetcupDnsProviderAdapter>();
        services.AddSingleton<IDnsProviderAdapter, CommonRestDnsProviderAdapter>();
        services.AddSingleton<DnsProviderRuntimeService>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<PublicIpAddressResolver>();
        services.AddSingleton<DdnsService>();
        services.AddSingleton<HealthProbeService>();
        services.AddSingleton<BackupDiagnosticsService>();
        services.AddSingleton<OperationsCommandService>();
        services.AddHttpClient("dns-providers", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(operationsOptions.ProviderTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caddy-UI-DNS/2.1");
        });
        services.AddHttpClient("notifications", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caddy-UI-Notifications/2.1");
        });
        services.AddHttpClient("health-probes", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
        });
        services.AddHttpClient("public-ip", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caddy-UI-DDNS/2.1");
        });
        services.AddSingleton<CaddyCertificateSourceRefreshWorker>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<CaddyCertificateSourceRefreshWorker>());
        services.AddSingleton<CaddyApplyService>();
        services.AddHostedService<SystemJobWorker>();

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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caddy-UI/2.1");
        });
        services.AddHostedService<IpIntelligenceRefreshWorker>();
        services.AddHostedService<ClientRiskAssessmentWorker>();

        services.AddSingleton(cutoverOptions);
        services.AddSingleton<CutoverReadinessService>();

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
