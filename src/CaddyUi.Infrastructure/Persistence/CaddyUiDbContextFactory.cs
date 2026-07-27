using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CaddyUi.Infrastructure.Persistence;

public sealed class CaddyUiDbContextFactory : IDesignTimeDbContextFactory<CaddyUiDbContext>
{
    public CaddyUiDbContext CreateDbContext(string[] args)
    {
        _ = args;

        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__CaddyUi") ??
            DependencyInjection.DefaultConnectionString;

        var options = new DbContextOptionsBuilder<CaddyUiDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsAssembly(typeof(CaddyUiDbContext).Assembly.FullName))
            .Options;

        return new CaddyUiDbContext(options);
    }
}
