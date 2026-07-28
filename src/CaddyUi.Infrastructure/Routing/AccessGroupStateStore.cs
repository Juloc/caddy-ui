using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Routing;

public sealed class AccessGroupStateStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public AccessGroupStateStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> IsEnabledAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        if (groupId == Guid.Empty)
        {
            return false;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM caddy_ui.access_groups
                    WHERE id = {0}
                      AND enabled) AS "Value"
                """,
                groupId)
            .SingleAsync(cancellationToken);
    }
}
