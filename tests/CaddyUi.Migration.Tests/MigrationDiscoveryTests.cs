using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Migration.Tests;

public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void InfrastructureAssembly_ContainsInitialMigration()
    {
        using var database = new CaddyUiDbContextFactory()
            .CreateDbContext(Array.Empty<string>());

        Assert.Contains(
            "20260727220000_PhaseOneFoundation",
            database.Database.GetMigrations());
    }
}
