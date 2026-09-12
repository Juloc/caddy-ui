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
    private static readonly string[] DnsPersistenceMethods =
    [
        "GetProviderAsync",
        "RecordProviderTestAsync",
        "ListDnsRecordsAsync",
        "CreateDnsRecordAsync",
        "SetDnsRecordEnabledAsync",
        "MarkDnsRecordSyncAsync",
        "ListDdnsTargetsAsync",
        "CreateDdnsTargetAsync",
        "SetDdnsTargetEnabledAsync",
        "ClaimDueDdnsTargetAsync",
        "CompleteDdnsTargetAsync",
    ];

    private static readonly string[] NotificationPersistenceMethods =
    [
        "ListNotificationChannelsAsync",
        "CreateNotificationChannelAsync",
        "SetNotificationChannelEnabledAsync",
        "RecordNotificationChannelTestAsync",
        "InsertNotificationAsync",
    ];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_operations_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_operations_tests")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public void DnsOperationsStore_OwnsDnsPersistenceBoundary()
    {
        var operationsMethods = typeof(OperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var dnsMethods = typeof(DnsOperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in DnsPersistenceMethods)
        {
            Assert.DoesNotContain(method, operationsMethods);
            Assert.Contains(method, dnsMethods);
        }
    }

    [Fact]
    public void NotificationOperationsStore_OwnsNotificationPersistenceBoundary()
    {
        var operationsMethods = typeof(OperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var notificationMethods = typeof(NotificationOperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in NotificationPersistenceMethods)
        {
            Assert.DoesNotContain(method, operationsMethods);
            Assert.Contains(method, notificationMethods);
        }
    }

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
        var store = new DnsOperationsStore(factory);

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
        var store = new DnsOperationsStore(factory);
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
    public async Task NotificationStore_PersistsChannelStateAndDurableNotification()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var store = new NotificationOperationsStore(factory);
        var channelId = await store.CreateNotificationChannelAsync(
            "Operations webhook",
            "webhook",
            "{\"url\":\"https://example.com/hook\"}",
            "{}");

        var channel = Assert.Single(await store.ListNotificationChannelsAsync());
        Assert.Equal(channelId, channel.Id);
        Assert.Equal("webhook", channel.ChannelType);
        Assert.True(channel.Enabled);

        await store.RecordNotificationChannelTestAsync(
            channelId,
            ProviderOperationResult.Failure("test failure"));
        channel = Assert.Single(await store.ListNotificationChannelsAsync());
        Assert.Equal("failed", channel.LastTestStatus);
        Assert.Equal("test failure", channel.LastTestError);

        await store.SetNotificationChannelEnabledAsync(channelId, false);
        channel = Assert.Single(await store.ListNotificationChannelsAsync());
        Assert.False(channel.Enabled);

        await store.InsertNotificationAsync(new SystemNotification(
            "warning",
            "persistence.test",
            "Persistence test",
            "Stored message",
            "test",
            "notification-1"));

        await using var verification = factory.CreateDbContext();
        var connection = verification.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT severity, event_type, title, message, object_type, object_id
            FROM caddy_ui.notifications
            WHERE event_type = 'persistence.test'
            LIMIT 1
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("warning", reader.GetString(0));
        Assert.Equal("persistence.test", reader.GetString(1));
        Assert.Equal("Persistence test", reader.GetString(2));
        Assert.Equal("Stored message", reader.GetString(3));
        Assert.Equal("test", reader.GetString(4));
        Assert.Equal("notification-1", reader.GetString(5));
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
