using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyUi.Application.Dns;

namespace CaddyUi.Infrastructure.Management;

public sealed partial class DomainProviderStore
{
    public async Task UpdateDnsChallengeTimingAsync(
        Guid providerId,
        string? propagationDelay,
        string? propagationTimeout,
        CancellationToken cancellationToken = default)
    {
        var normalizedDelay = DnsChallengeTiming.NormalizeDelay(propagationDelay);
        var normalizedTimeout = DnsChallengeTiming.NormalizeTimeout(propagationTimeout);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        string providerType;
        string configJson;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText =
                """
                SELECT provider_type, config_json::text
                FROM caddy_ui.dns_providers
                WHERE id = @id
                """;
            AddParameter(read, "id", providerId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Der DNS-Provider existiert nicht mehr.");
            }

            providerType = reader.GetString(0);
            configJson = reader.GetString(1);
        }

        var definition = DnsProviderCatalog.Find(providerType) ??
            throw new InvalidOperationException("Der DNS-Provider-Typ wird nicht mehr unterstützt.");
        if (!definition.Capabilities.HasFlag(DnsProviderCapability.DnsChallenge))
        {
            throw new InvalidOperationException("Dieser Provider unterstützt keine DNS-01-Challenge.");
        }

        JsonObject configuration;
        try
        {
            configuration = JsonNode.Parse(configJson) as JsonObject ??
                throw new InvalidOperationException("Die Provider-Konfiguration ist kein JSON-Objekt.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Die Provider-Konfiguration ist beschädigt.", exception);
        }

        SetOrRemove(configuration, DnsChallengeTiming.PropagationDelayKey, normalizedDelay);
        SetOrRemove(configuration, DnsChallengeTiming.PropagationTimeoutKey, normalizedTimeout);

        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE caddy_ui.dns_providers
            SET config_json = CAST(@config_json AS jsonb),
                updated_at = @updated_at
            WHERE id = @id
            """;
        AddParameter(update, "config_json", configuration.ToJsonString());
        AddParameter(update, "updated_at", DateTimeOffset.UtcNow);
        AddParameter(update, "id", providerId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Der DNS-Provider konnte nicht aktualisiert werden.");
        }
    }

    private static void SetOrRemove(JsonObject configuration, string key, string value)
    {
        if (value.Length == 0)
        {
            configuration.Remove(key);
            return;
        }

        configuration[key] = value;
    }
}
