using CaddyUi.Domain.Certificates;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class OperationsStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_operations_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_operations_tests")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task DnsRecord_RequiresTheDomainsAssignedProvider()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var management = new DomainProviderStore(factory);
        var netcupId = await management.CreateProviderAsync(
            "netcup",
            "Netcup",
            "{\"customer_number\":\"123456\"}",
            "{\"api_key\":\"NETCUP_API_KEY\",\"api_password\":\"NETCUP_API_PASSWORD\"}");
        var cloudflareId = await management.CreateProviderAsync(
            "cloudflare",
            "Cloudflare",
            "{}",
            "{\"api_token\":\"CLOUDFLARE_API_TOKEN\"}");
        var domainId = await management.CreateDomainAsync(
            "example.com",
            "Example",
            netcupId,
            CertificateMode.Wildcard);
        var store = new OperationsStore(factory);

        await store.CreateDnsRecordAsync(
            domainId,
            netcupId,
            "app",
            "A",
            "203.0.113.10",
            300,
            null);

        var record = Assert.Single(await store.ListDnsRecordsAsync());
        Assert.Equal("app.example.com", record.Fqdn);
        Assert.Equal("pending", record.LastSyncStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateDnsRecordAsync(
            domainId,
            cloudflareId,
            "other",
            "A",
            "203.0.113.11",
            300,
            null));
    }

    [Fact]
    public async Task DdnsClaim_IsExclusiveAndMovesNextRunForward()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var management = new DomainProviderStore(factory);
        var providerId = await management.CreateProviderAsync(
            "netcup",
            "Netcup DDNS",
            "{\"customer_number\":\"123456\"}",
            "{\"api_key\":\"NETCUP_API_KEY\",\"api_password\":\"NETCUP_API_PASSWORD\"}");
        var domainId = await management.CreateDomainAsync(
            "ddns.example",
            "DDNS",
            providerId);
        var store = new OperationsStore(factory);
        await store.CreateDdnsTargetAsync(
            domainId,
            providerId,
            "home",
            "A",
            300,
            "static",
            "203.0.113.25");

        var claimed = await store.ClaimDueDdnsTargetAsync("worker-a");
        var second = await store.ClaimDueDdnsTargetAsync("worker-b");

        Assert.NotNull(claimed);
        Assert.Null(second);
        Assert.Equal("running", (await store.ListDdnsTargetsAsync()).Single().LastStatus);
    }

    [Fact]
    public async Task SecretResolver_ReadsEnvironmentReferenceWithoutPersistingTheValue()
    {
        const string variable = "CADDY_UI_TEST_PHASE8_SECRET";
        Environment.SetEnvironmentVariable(variable, "private-value");
        try
        {
            var resolver = CreateSecretResolver();

            var value = await resolver.ResolveAsync($"secret://env/{variable}");

            Assert.Equal("private-value", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task SecretResolver_ProtectsAndResolvesUiEnteredSecret()
    {
        var resolver = CreateSecretResolver();

        var reference = resolver.ProtectOrReference("netcup-private-value");
        var value = await resolver.ResolveAsync(reference);

        Assert.StartsWith("secret://protected/v1/", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("netcup-private-value", reference, StringComparison.Ordinal);
        Assert.Equal("netcup-private-value", value);
    }

    private static SecretReferenceResolver CreateSecretResolver()
    {
        return new SecretReferenceResolver(new EphemeralDataProtectionProvider());
    }
}
