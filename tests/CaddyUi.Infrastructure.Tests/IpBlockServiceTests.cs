using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class IpBlockServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_block_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_block_tests")
        .Build();

    public Task InitializeAsync()
    {
        return _postgres.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _postgres.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task BlockAndUnblock_UpdateDatabaseFileHistoryAndAudit()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"caddy-ui-block-service-{Guid.NewGuid():N}");
        var blocklistPath = Path.Combine(directory, "blocked.txt");
        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        var options = new IpSecurityOptions
        {
            BlockWriteMode = IpBlockWriteMode.Shadow,
            BlocklistPath = blocklistPath,
            MaximumBlockHours = 720,
        };
        var service = new IpBlockService(
            factory,
            new AtomicBlocklistWriter(),
            options,
            new FixedTimeProvider(now));

        try
        {
            var block = await service.BlockAsync(
                "203.0.113.10",
                "Scanner",
                now.AddHours(24),
                null,
                "integration-test",
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal("203.0.113.10/32", block.Target);
            Assert.Equal("shadow", block.ActivationState);
            var blocklist = await File.ReadAllTextAsync(
                blocklistPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("203.0.113.10", blocklist, StringComparison.Ordinal);

            var released = await service.UnblockAsync(
                block.Id,
                "False positive",
                null,
                "integration-test",
                null,
                TestContext.Current.CancellationToken);
            Assert.Equal("released", released.ActivationState);
            Assert.NotNull(released.ReleasedAt);
            Assert.Equal(
                string.Empty,
                await File.ReadAllTextAsync(
                    blocklistPath,
                    TestContext.Current.CancellationToken));

            await using var verification = factory.CreateDbContext();
            Assert.Equal(
                2,
                await verification.Database
                    .SqlQueryRaw<long>(
                        "SELECT COUNT(*) AS \"Value\" FROM caddy_ui.ip_block_history")
                    .SingleAsync());
            Assert.Equal(
                2,
                await verification.Database
                    .SqlQueryRaw<long>(
                        "SELECT COUNT(*) AS \"Value\" FROM caddy_ui.audit_events WHERE object_type = 'ip-block-rule'")
                    .SingleAsync());
            Assert.Equal(
                2,
                await verification.Database
                    .SqlQueryRaw<long>(
                        "SELECT COUNT(*) AS \"Value\" FROM caddy_ui.security_events WHERE kind IN ('ip-block', 'ip-unblock')")
                    .SingleAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}
