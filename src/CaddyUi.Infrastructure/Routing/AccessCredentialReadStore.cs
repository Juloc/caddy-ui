using System.Data;
using System.Data.Common;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Routing;

public sealed class AccessCredentialReadStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public AccessCredentialReadStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<AccessCredentialRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, group_id, username, enabled, created_at, updated_at
            FROM caddy_ui.access_credentials
            ORDER BY lower(username), id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AccessCredentialRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AccessCredentialRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                ReadTimestamp(reader, 4),
                ReadTimestamp(reader, 5)));
        }

        return result;
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetFieldValue<DateTime>(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
