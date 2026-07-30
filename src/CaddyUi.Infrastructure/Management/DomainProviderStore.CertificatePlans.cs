using System.Text.Json;
using CaddyUi.Domain.Certificates;
using CaddyUi.Domain.Domains;

namespace CaddyUi.Infrastructure.Management;

public sealed partial class DomainProviderStore
{
    public async Task<Guid> CreateDomainWithCertificatePlanAsync(
        string name,
        string displayName,
        Guid? dnsProviderId,
        bool requestWildcardCertificate,
        bool requestBaseCertificate,
        bool makeDefault = false,
        CancellationToken cancellationToken = default)
    {
        if (!requestWildcardCertificate && !requestBaseCertificate)
        {
            throw new ArgumentException("Mindestens Wildcard oder Basisdomain-Zertifikat muss ausgewählt sein.");
        }

        var defaultMode = requestWildcardCertificate
            ? CertificateMode.Wildcard
            : CertificateMode.Individual;
        var domain = ManagedDomain.Create(
            name,
            displayName,
            defaultMode,
            dnsProviderId: dnsProviderId);
        var configJson = JsonSerializer.Serialize(new
        {
            schema = "managed-domain-v1",
            certificatePlan = new
            {
                wildcard = requestWildcardCertificate,
                baseDomain = requestBaseCertificate,
            },
        });

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (dnsProviderId is Guid providerId)
            {
                await using var provider = connection.CreateCommand();
                provider.Transaction = transaction;
                provider.CommandText = "SELECT COUNT(*) FROM caddy_ui.dns_providers WHERE id = @id AND enabled";
                AddParameter(provider, "id", providerId);
                var count = Convert.ToInt32(
                    await provider.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (count != 1)
                {
                    throw new InvalidOperationException("Der ausgewählte DNS-Provider existiert nicht oder ist deaktiviert.");
                }
            }

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
                    @default_certificate_mode, @dns_provider_id, CAST(@config_json AS jsonb),
                    @created_at, @updated_at)
                """;
            AddParameter(insert, "id", domain.Id);
            AddParameter(insert, "name", domain.Name);
            AddParameter(insert, "display_name", domain.DisplayName);
            AddParameter(insert, "is_default", isDefault);
            AddParameter(insert, "default_certificate_mode", domain.DefaultCertificateMode.ToStorageValue());
            AddParameter(insert, "dns_provider_id", dnsProviderId);
            AddParameter(insert, "config_json", configJson);
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
}
