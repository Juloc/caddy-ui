using System.Data;
using System.Data.Common;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Security;

public sealed class UserPreferenceStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public UserPreferenceStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<string> GetLanguageAsync(
        Guid userId,
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
            "SELECT language FROM caddy_ui.users WHERE id = @id LIMIT 1";
        AddParameter(command, "id", userId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string language && !string.IsNullOrWhiteSpace(language)
            ? language
            : "en";
    }

    public async Task SetLanguageAsync(
        Guid userId,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE caddy_ui.users
            SET language = @language,
                updated_at = @updated_at
            WHERE id = @id
            """;
        AddParameter(command, "language", language.Trim().ToLowerInvariant());
        AddParameter(command, "updated_at", DateTimeOffset.UtcNow);
        AddParameter(command, "id", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The user account no longer exists.");
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
