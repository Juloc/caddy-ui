using CaddyUi.Application;

namespace CaddyUi.Application.Tests;

public sealed class FoundationStatusServiceTests
{
    [Fact]
    public void GetStatus_ReturnsOperationalFoundation()
    {
        var service = new FoundationStatusService();

        var status = service.GetStatus();

        Assert.True(status.IsOperational);
        Assert.Equal("Caddy UI", status.Product);
        Assert.Contains("PostgreSQL", status.Runtime, StringComparison.Ordinal);
    }
}
