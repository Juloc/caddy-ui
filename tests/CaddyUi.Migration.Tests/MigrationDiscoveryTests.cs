using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Migration.Tests;

public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void InfrastructureAssembly_ContainsAllPublishedMigrations()
    {
        using var database = new CaddyUiDbContextFactory()
            .CreateDbContext(Array.Empty<string>());

        var migrations = database.Database.GetMigrations().ToArray();

        Assert.Contains(
            "20260727220000_PhaseOneFoundation",
            migrations);
        Assert.Contains(
            "20260728220000_PhaseTwoCorePersistence",
            migrations);
        Assert.Contains(
            "20260728220100_PhaseTwoAnalyticsPersistence",
            migrations);
        Assert.Contains(
            "20260728220200_PhaseTwoSecurityPersistence",
            migrations);
        Assert.Contains(
            "20260728220300_PhaseTwoMigrationPersistence",
            migrations);
        Assert.Contains(
            "20260728230000_PhaseThreeAuthenticationAndDomainManagement",
            migrations);
        Assert.Contains(
            "20260728240000_PhaseFourAnalyticsRuntime",
            migrations);
        Assert.Contains(
            "20260728250000_PhaseFiveIpSecurity",
            migrations);
        Assert.Contains(
            "20260728270000_PhaseSevenRouteManagement",
            migrations);
    }
}
