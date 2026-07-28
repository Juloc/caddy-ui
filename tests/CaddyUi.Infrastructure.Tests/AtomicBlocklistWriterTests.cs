using System.Net;
using CaddyUi.Infrastructure.Security;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AtomicBlocklistWriterTests
{
    [Fact]
    public async Task ApplyAndRollback_PreserveValidBlocklist()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"caddy-ui-blocklist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "blocked.txt");
        await File.WriteAllTextAsync(path, "198.51.100.1|2030-01-01T00:00:00.0000000+00:00|existing\n");
        var writer = new AtomicBlocklistWriter();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);

        try
        {
            var receipt = await writer.ApplyAsync(
                path,
                [
                    new BlocklistEntry(
                        IPAddress.Parse("203.0.113.10"),
                        expiresAt,
                        "manual|reason"),
                ],
                TestContext.Current.CancellationToken);
            var applied = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("203.0.113.10", applied, StringComparison.Ordinal);
            Assert.Contains("manual/reason", applied, StringComparison.Ordinal);
            Assert.DoesNotContain("198.51.100.1", applied, StringComparison.Ordinal);

            await writer.RollbackAsync(receipt, TestContext.Current.CancellationToken);
            var rolledBack = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("198.51.100.1", rolledBack, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
