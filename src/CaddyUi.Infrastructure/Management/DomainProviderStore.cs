using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaddyUi.Application.Dns;
using CaddyUi.Domain.Certificates;
using CaddyUi.Domain.Domains;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Management;

public sealed partial class DomainProviderStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public DomainProviderStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<DnsProviderRecord>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, provider_type, label, enabled, config_json::text,
                   secret_references_json::text, last_tested_at,
                   last_test_status, last_test_error
            FROM caddy_ui.dns_providers
            ORDER BY lower(label), id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DnsProviderRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DnsProviderRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return result;
    }

    public async Task<Guid> CreateProviderAsync(
        string providerType,
        string label,
        string configJson,
        string secretReferencesJson,
        CancellationToken cancellationToken = default)
    {
        var definition = DnsProviderCatalog.Find(providerType) ??
            throw new ArgumentException("The selected DNS provider is not supported.", nameof(providerType));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var normalizedConfig = NormalizeObjectJson(configJson, "provider configuration");
        var normalizedSecrets = NormalizeSecretReferences(secretReferencesJson, definition);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.dns_providers(
                id, provider_type, label, config_json, created_at, updated_at,
                enabled, secret_references_json, last_test_status, last_test_error)
            VALUES(
                @id, @provider_type, @label, CAST(@config_json AS jsonb),
                @created_at, @updated_at, true, CAST(@secret_references_json AS jsonb),
                'untested', '')
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "provider_type", definition.Type);
                AddParameter(command, "label", label.Trim());
                AddParameter(command, "config_json", normalizedConfig);
                AddParameter(command, "created_at", now);
                AddParameter(command, "updated_at", now);
                AddParameter(command, "secret_references_json", normalizedSecrets);
            },
            cancellationToken);

        return id;
    }

    public Task SetProviderEnabledAsync(
        Guid providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            UPDATE caddy_ui.dns_providers
            SET enabled = @enabled, updated_at = @updated_at
            WHERE id = @id
            """,
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "updated_at", DateTimeOffset.UtcNow);
                AddParameter(command, "id", providerId);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedDomainRecord>> ListDomainsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT domains.id, domains.name, domains.display_name, domains.enabled,
                   domains.is_default, domains.default_certificate_mode,
                   domains.dns_provider_id, providers.label,
                   COUNT(routes.id)::integer
            FROM caddy_ui.managed_domains AS domains
            LEFT JOIN caddy_ui.dns_providers AS providers
              ON providers.id = domains.dns_provider_id
            LEFT JOIN caddy_ui.managed_routes AS routes
              ON routes.domain_id = domains.id
            GROUP BY domains.id, providers.label
            ORDER BY domains.is_default DESC, lower(domains.name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ManagedDomainRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ManagedDomainRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<Guid> CreateDomainAsync(
        string name,
        string displayName,
        Guid? dnsProviderId,
        CertificateMode defaultCertificateMode = CertificateMode.Wildcard,
        bool makeDefault = false,
        CancellationToken cancellationToken = default)
    {
        var domain = ManagedDomain.Create(
            name,
            displayName,
            defaultCertificateMode,
            dnsProviderId: dnsProviderId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var domainCount = await CountDomainsAsync(connection, transaction, cancellationToken);
            var isDefault = makeDefault || domainCount == 0;
            if (isDefault)
            {
                await using var reset = connection.CreateCommand();
                reset.Transaction = transaction;
                reset.CommandText = "UPDATE caddy_ui.managed_domains SET is_default = false WHERE is_default";
                await reset.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO caddy_ui.managed_domains(
                    id, name, display_name, enabled, is_default,
                    default_certificate_mode, dns_provider_id, config_json,
                    created_at, updated_at)
                VALUES(
                    @id, @name, @display_name, true, @is_default,
                    @default_certificate_mode, @dns_provider_id, '{}'::jsonb,
                    @created_at, @updated_at)
                """;
            AddParameter(insert, "id", domain.Id);
            AddParameter(insert, "name", domain.Name);
            AddParameter(insert, "display_name", domain.DisplayName);
            AddParameter(insert, "is_default", isDefault);
            AddParameter(insert, "default_certificate_mode", domain.DefaultCertificateMode.ToStorageValue());
            AddParameter(insert, "dns_provider_id", dnsProviderId);
            AddParameter(insert, "created_at", DateTimeOffset.UtcNow);
            AddParameter(insert, "updated_at", DateTimeOffset.UtcNow);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return domain.Id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetDefaultDomainAsync(
        Guid domainId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var reset = connection.CreateCommand())
            {
                reset.Transaction = transaction;
                reset.CommandText = "UPDATE caddy_ui.managed_domains SET is_default = false WHERE is_default";
                await reset.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var set = connection.CreateCommand();
            set.Transaction = transaction;
            set.CommandText =
                """
                UPDATE caddy_ui.managed_domains
                SET is_default = true, updated_at = @updated_at
                WHERE id = @id AND enabled
                """;
            AddParameter(set, "updated_at", DateTimeOffset.UtcNow);
            AddParameter(set, "id", domainId);
            if (await set.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected domain does not exist or is disabled.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<int> CountDomainsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM caddy_ui.managed_domains";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeObjectJson(string? value, string description)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "{}" : value;
        using var document = JsonDocument.Parse(candidate);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"The {description} must be a JSON object.", nameof(value));
        }

        return document.RootElement.GetRawText();
    }

    private static string NormalizeSecretReferences(
        string? value,
        DnsProviderDefinition definition)
    {
        var normalized = NormalizeObjectJson(value, "secret references");
        using var document = JsonDocument.Parse(normalized);
        var values = document.RootElement
            .EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal);

        foreach (var required in definition.Secrets.Where(field => field.Required))
        {
            if (!values.TryGetValue(required.Key, out var reference) || string.IsNullOrWhiteSpace(reference))
            {
                throw new ArgumentException(
                    $"A secret reference is required for '{required.Key}'.",
                    nameof(value));
            }
        }

        foreach (var reference in values.Values)
        {
            if (!SecretReferencePattern().IsMatch(reference) &&
                !reference.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Secret references must be environment-variable names or secret:// references.",
                    nameof(value));
            }
        }

        return normalized;
    }

    private static async Task<DbConnection> OpenConnectionAsync(
        CaddyUiDbContext context,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                CultureInfo.InvariantCulture),
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();
}
