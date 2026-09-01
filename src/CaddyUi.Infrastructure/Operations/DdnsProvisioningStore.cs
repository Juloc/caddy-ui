using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Operations;

public sealed record DdnsTargetConfiguration(
    string Name,
    string RecordType,
    string AddressSource = "public",
    string StaticValue = "",
    bool Enabled = true);

public sealed class DdnsProvisioningStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public DdnsProvisioningStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<Guid>> UpsertTargetsAsync(
        Guid domainId,
        Guid providerId,
        IEnumerable<DdnsTargetConfiguration> targets,
        int intervalSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var normalizedTargets = targets
            .Select(NormalizeTarget)
            .DistinctBy(target => (target.Name.ToLowerInvariant(), target.RecordType))
            .ToArray();
        if (normalizedTargets.Length == 0)
        {
            throw new ArgumentException("At least one DDNS target is required.", nameof(targets));
        }

        var normalizedInterval = Math.Clamp(intervalSeconds, 60, 86400);
        var now = DateTimeOffset.UtcNow;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureDomainProviderAssignmentAsync(
                connection,
                transaction,
                domainId,
                providerId,
                cancellationToken);

            var result = new List<Guid>(normalizedTargets.Length);
            foreach (var target in normalizedTargets)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO caddy_ui.ddns_targets(
                        id, domain_id, provider_id, name, record_type, enabled,
                        interval_seconds, address_source, static_value, last_value,
                        next_run_at, last_run_at, last_status, last_error,
                        created_at, updated_at)
                    VALUES(
                        @id, @domain_id, @provider_id, @name, @record_type, @enabled,
                        @interval_seconds, @address_source, @static_value, '',
                        @now, NULL, 'pending', '', @now, @now)
                    ON CONFLICT (domain_id, lower(name), record_type)
                    DO UPDATE SET
                        provider_id = EXCLUDED.provider_id,
                        enabled = EXCLUDED.enabled,
                        interval_seconds = EXCLUDED.interval_seconds,
                        address_source = EXCLUDED.address_source,
                        static_value = EXCLUDED.static_value,
                        last_value = '',
                        next_run_at = EXCLUDED.next_run_at,
                        last_status = 'pending',
                        last_error = '',
                        updated_at = EXCLUDED.updated_at
                    RETURNING id
                    """;
                var candidateId = Guid.NewGuid();
                AddParameter(command, "id", candidateId);
                AddParameter(command, "domain_id", domainId);
                AddParameter(command, "provider_id", providerId);
                AddParameter(command, "name", target.Name);
                AddParameter(command, "record_type", target.RecordType);
                AddParameter(command, "enabled", target.Enabled);
                AddParameter(command, "interval_seconds", normalizedInterval);
                AddParameter(command, "address_source", target.AddressSource);
                AddParameter(command, "static_value", target.StaticValue);
                AddParameter(command, "now", now);

                var id = await command.ExecuteScalarAsync(cancellationToken);
                if (id is not Guid targetId)
                {
                    throw new InvalidOperationException("The DDNS target could not be persisted.");
                }

                result.Add(targetId);
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task EnsureDomainProviderAssignmentAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid domainId,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM caddy_ui.managed_domains AS domains
            JOIN caddy_ui.dns_providers AS providers ON providers.id = @provider_id
            WHERE domains.id = @domain_id
              AND domains.dns_provider_id = providers.id
              AND domains.enabled
              AND providers.enabled
            """;
        AddParameter(command, "domain_id", domainId);
        AddParameter(command, "provider_id", providerId);

        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException(
                "The selected domain has no matching enabled DNS provider assignment.");
        }
    }

    private static DdnsTargetConfiguration NormalizeTarget(DdnsTargetConfiguration target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var name = string.IsNullOrWhiteSpace(target.Name) ? "@" : target.Name.Trim().TrimEnd('.');
        if (name.Length > 253 || name.Contains(' ') || name.Contains('\r') || name.Contains('\n'))
        {
            throw new ArgumentException("The DDNS record name is invalid.", nameof(target));
        }

        var type = target.RecordType.Trim().ToUpperInvariant();
        if (type is not ("A" or "AAAA"))
        {
            throw new ArgumentException("DDNS supports A and AAAA records only.", nameof(target));
        }

        var source = target.AddressSource.Trim().ToLowerInvariant() == "static" ? "static" : "public";
        var staticValue = source == "static" ? target.StaticValue.Trim() : string.Empty;
        if (source == "static")
        {
            if (!IPAddress.TryParse(staticValue, out var address))
            {
                throw new ArgumentException("A valid static IP address is required.", nameof(target));
            }

            var expectedFamily = type == "AAAA" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
            if (address.AddressFamily != expectedFamily)
            {
                throw new ArgumentException($"The static address does not match record type {type}.", nameof(target));
            }
        }

        return target with
        {
            Name = name,
            RecordType = type,
            AddressSource = source,
            StaticValue = staticValue,
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
