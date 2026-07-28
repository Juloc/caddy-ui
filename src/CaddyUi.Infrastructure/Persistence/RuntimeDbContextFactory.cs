using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Persistence;

public sealed class RuntimeDbContextFactory : IDbContextFactory<CaddyUiDbContext>
{
    private readonly DbContextOptions<CaddyUiDbContext> _options;

    public RuntimeDbContextFactory(string connectionString)
    {
        _options = new DbContextOptionsBuilder<CaddyUiDbContext>()
            .UseNpgsql(
                connectionString,
                postgres =>
                {
                    postgres.MigrationsAssembly(typeof(CaddyUiDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__EFMigrationsHistory", "public");
                })
            .Options;
    }

    public CaddyUiDbContext CreateDbContext()
    {
        return new CaddyUiDbContext(_options);
    }

    public Task<CaddyUiDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
