using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Analytics;

public sealed class AnalyticsClientKeyProvider : IDisposable
{
    private const string SettingKey = "analytics.client_hash_key";
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _cachedKey;

    public AnalyticsClientKeyProvider(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _contextFactory = contextFactory;
        _protector = dataProtectionProvider.CreateProtector(
            "CaddyUi.Analytics.ClientHashKey.v1");
    }

    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedKey is not null)
        {
            return _cachedKey.ToArray();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedKey is not null)
            {
                return _cachedKey.ToArray();
            }

            var existing = await ReadProtectedValueAsync(cancellationToken);
            if (existing is not null)
            {
                _cachedKey = Convert.FromBase64String(_protector.Unprotect(existing));
                return _cachedKey.ToArray();
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            var protectedValue = _protector.Protect(Convert.ToBase64String(generated));
            await TryCreateAsync(protectedValue, cancellationToken);

            existing = await ReadProtectedValueAsync(cancellationToken);
            _cachedKey = existing is null
                ? generated
                : Convert.FromBase64String(_protector.Unprotect(existing));
            return _cachedKey.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<string?> ReadProtectedValueAsync(CancellationToken cancellationToken)
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
            SELECT value_json::text
            FROM caddy_ui.application_settings
            WHERE key = @key
            """;
        AddParameter(command, "key", SettingKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            Convert.ToString(
                result,
                System.Globalization.CultureInfo.InvariantCulture) ??
            "{}");
        return document.RootElement.TryGetProperty("protected", out var value)
            ? value.GetString()
            : null;
    }

    private async Task TryCreateAsync(
        string protectedValue,
        CancellationToken cancellationToken)
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
            INSERT INTO caddy_ui.application_settings(key, value_json, updated_at)
            VALUES(@key, CAST(@value_json AS jsonb), @updated_at)
            ON CONFLICT (key) DO NOTHING
            """;
        AddParameter(command, "key", SettingKey);
        AddParameter(
            command,
            "value_json",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["protected"] = protectedValue,
            }));
        AddParameter(command, "updated_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
