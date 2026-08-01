using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Tests;

public sealed class ManagedRouteReconciliationWorkerTests
{
    [Fact]
    public void RequiresReconciliation_IgnoresOnlyLineEndingDifferences()
    {
        Assert.False(ManagedRouteReconciliationWorker.RequiresReconciliation(
            "example.com {\r\n}\r\n",
            "example.com {\n}\n"));
        Assert.True(ManagedRouteReconciliationWorker.RequiresReconciliation(
            "example.com {\n}\n",
            "example.com {\n    respond \"ok\" 200\n}\n"));
    }

    [Theory]
    [InlineData(null, "http://caddy:2019/config/")]
    [InlineData("caddy:2019", "http://caddy:2019/config/")]
    [InlineData("https://127.0.0.1:2019/custom", "https://127.0.0.1:2019/config/")]
    public void BuildAdminConfigurationUri_NormalizesAddress(
        string? value,
        string expected)
    {
        Assert.Equal(
            new Uri(expected),
            ManagedRouteReconciliationWorker.BuildAdminConfigurationUri(value));
    }

    [Fact]
    public void BuildAdminConfigurationUri_RejectsUnsupportedScheme()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ManagedRouteReconciliationWorker.BuildAdminConfigurationUri("ftp://caddy:2019"));
    }
}
