using System.Data;
using System.Data.Common;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Certificates;

public sealed record CertificateStatusItem(
    string Kind,
    string Name,
    bool Requested,
    string State,
    string Label,
    string Detail,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    int? DaysRemaining);

public sealed record DomainCertificateStatus(
    Guid DomainId,
    string DomainName,
    IReadOnlyList<CertificateStatusItem> Certificates);

public sealed class CertificateStatusService
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly OperationsOptions _options;

    public CertificateStatusService(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        OperationsOptions options)
    {
        _contextFactory = contextFactory;
        _options = options;
    }

    public async Task<IReadOnlyDictionary<Guid, DomainCertificateStatus>> GetDomainStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var inventory = ReadInventory();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var appliedNames = await ReadAppliedCertificateNamesAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT domains.id,
                   domains.name,
                   domains.default_certificate_mode,
                   domains.config_json::text,
                   providers.provider_type,
                   providers.enabled,
                   providers.last_test_status,
                   providers.last_test_error
            FROM caddy_ui.managed_domains AS domains
            LEFT JOIN caddy_ui.dns_providers AS providers
              ON providers.id = domains.dns_provider_id
            WHERE domains.enabled
            ORDER BY lower(domains.name), domains.id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<Guid, DomainCertificateStatus>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var domainId = reader.GetGuid(0);
            var domainName = reader.GetString(1);
            var plan = ReadPlan(reader.GetString(2), reader.GetString(3));
            var provider = reader.IsDBNull(4)
                ? null
                : new ProviderStatus(
                    reader.GetString(4),
                    reader.GetBoolean(5),
                    reader.GetString(6),
                    reader.GetString(7));
            var certificates = new[]
            {
                BuildStatus(
                    "wildcard",
                    $"*.{domainName}",
                    plan.Wildcard,
                    provider,
                    inventory,
                    appliedNames),
                BuildStatus(
                    "base",
                    domainName,
                    plan.BaseDomain,
                    provider: null,
                    inventory,
                    appliedNames),
            };
            result[domainId] = new DomainCertificateStatus(domainId, domainName, certificates);
        }

        return result;
    }

    private CertificateStatusItem BuildStatus(
        string kind,
        string name,
        bool requested,
        ProviderStatus? provider,
        IReadOnlyDictionary<string, CertificateArtifact> inventory,
        IReadOnlySet<string> appliedNames)
    {
        if (!requested)
        {
            return new CertificateStatusItem(
                kind,
                name,
                false,
                "not-requested",
                "Nicht angefordert",
                "Kann später über die Domain-Einstellungen aktiviert werden.",
                null,
                null,
                null);
        }

        if (inventory.TryGetValue(name, out var artifact))
        {
            var now = DateTimeOffset.UtcNow;
            var days = (int)Math.Floor((artifact.ExpiresAt - now).TotalDays);
            if (artifact.ExpiresAt <= now)
            {
                return new CertificateStatusItem(
                    kind,
                    name,
                    true,
                    "expired",
                    "Abgelaufen",
                    $"Das gespeicherte Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen.",
                    artifact.NotBefore,
                    artifact.ExpiresAt,
                    days);
            }

            if (days <= 14)
            {
                return new CertificateStatusItem(
                    kind,
                    name,
                    true,
                    "renewal-due",
                    "Erneuerung fällig",
                    $"Noch {Math.Max(days, 0)} Tage gültig. Caddy sollte die Erneuerung automatisch durchführen.",
                    artifact.NotBefore,
                    artifact.ExpiresAt,
                    days);
            }

            return new CertificateStatusItem(
                kind,
                name,
                true,
                "active",
                "Vorhanden",
                $"Gültig bis {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC ({days} Tage).",
                artifact.NotBefore,
                artifact.ExpiresAt,
                days);
        }

        if (kind == "wildcard")
        {
            var providerProblem = ProviderProblem(provider);
            if (providerProblem is not null)
            {
                return new CertificateStatusItem(
                    kind,
                    name,
                    true,
                    "blocked",
                    "Beschaffung blockiert",
                    providerProblem,
                    null,
                    null,
                    null);
            }
        }

        if (appliedNames.Contains(name))
        {
            return new CertificateStatusItem(
                kind,
                name,
                true,
                "requested",
                "Angefordert",
                "Der Name ist im aktiven Caddy-Stand enthalten, aber noch nicht im Zertifikatsspeicher nachweisbar. Caddy-Logs prüfen, falls der Status bestehen bleibt.",
                null,
                null,
                null);
        }

        var detail = kind == "wildcard" && provider?.LastTestStatus == "untested"
            ? "Konfiguration ist vollständig, der Provider wurde aber noch nicht getestet. Danach Vorschau und Apply ausführen."
            : "Noch nicht im aktiven Caddy-Stand. Vorschau prüfen und anschließend Apply ausführen.";
        return new CertificateStatusItem(
            kind,
            name,
            true,
            "draft",
            "Bereit für Apply",
            detail,
            null,
            null,
            null);
    }

    private string? ProviderProblem(ProviderStatus? provider)
    {
        if (provider is null)
        {
            return "Kein DNS-Provider zugeordnet. Wildcard-Zertifikate benötigen DNS-01.";
        }

        if (!provider.Enabled)
        {
            return "Der zugeordnete DNS-Provider ist deaktiviert.";
        }

        if (!_options.InstalledCaddyDnsModules.Contains(provider.ProviderType))
        {
            return $"Das Caddy-DNS-Modul '{provider.ProviderType}' ist im laufenden Image nicht installiert.";
        }

        if (!string.Equals(provider.ProviderType, "netcup", StringComparison.OrdinalIgnoreCase))
        {
            return $"Für '{provider.ProviderType}' ist noch kein geprüfter Wildcard-Renderer aktiviert.";
        }

        if (provider.LastTestStatus == "failed")
        {
            return string.IsNullOrWhiteSpace(provider.LastTestError)
                ? "Der letzte Provider-Test ist fehlgeschlagen."
                : $"Der letzte Provider-Test ist fehlgeschlagen: {provider.LastTestError}";
        }

        return null;
    }

    private IReadOnlyDictionary<string, CertificateArtifact> ReadInventory()
    {
        var result = new Dictionary<string, CertificateArtifact>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_options.CertificateDirectory))
        {
            return result;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(
                _options.CertificateDirectory,
                "*.crt",
                SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                var artifact = new CertificateArtifact(
                    new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                    new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                    file);
                foreach (var name in CertificateNames(certificate, file))
                {
                    if (!result.TryGetValue(name, out var current) ||
                        artifact.ExpiresAt > current.ExpiresAt)
                    {
                        result[name] = artifact;
                    }
                }
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static IEnumerable<string> CertificateNames(X509Certificate2 certificate, string file)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddName(names, certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        AddName(names, certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        AddStorageName(names, Path.GetFileNameWithoutExtension(file));
        AddStorageName(names, Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty));

        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is not null)
        {
            try
            {
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
                var sequence = reader.ReadSequence();
                var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);
                while (sequence.HasData)
                {
                    if (sequence.PeekTag().HasSameClassAndValue(dnsTag))
                    {
                        AddName(
                            names,
                            sequence.ReadCharacterString(UniversalTagNumber.IA5String, dnsTag));
                    }
                    else
                    {
                        sequence.ReadEncodedValue();
                    }
                }
            }
            catch (AsnContentException)
            {
            }
        }

        return names;
    }

    private static void AddStorageName(ISet<string> names, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddName(
            names,
            value.Replace("wildcard_.", "*.", StringComparison.OrdinalIgnoreCase)
                .Replace("wildcard_", "*.", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddName(ISet<string> names, string? value)
    {
        var candidate = value?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        if (candidate.Length > 0 && candidate.Contains('.', StringComparison.Ordinal))
        {
            names.Add(candidate);
        }
    }

    private static async Task<IReadOnlySet<string>> ReadAppliedCertificateNamesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT manifest_json::text
            FROM caddy_ui.route_revisions
            WHERE applied
            ORDER BY created_at DESC
            LIMIT 1
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json || string.IsNullOrWhiteSpace(json))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("certificates", out var certificates) ||
                certificates.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var certificate in certificates.EnumerateArray())
            {
                if (certificate.TryGetProperty("name", out var name))
                {
                    AddName(result, name.GetString());
                }
                else if (certificate.TryGetProperty("host", out var host))
                {
                    AddName(result, host.GetString());
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static (bool Wildcard, bool BaseDomain) ReadPlan(string mode, string json)
    {
        var wildcard = string.Equals(mode, "wildcard", StringComparison.OrdinalIgnoreCase);
        var baseDomain = wildcard;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("certificatePlan", out var plan) ||
                plan.ValueKind != JsonValueKind.Object)
            {
                return (wildcard, baseDomain);
            }

            if (plan.TryGetProperty("wildcard", out var wildcardProperty) &&
                wildcardProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                wildcard = wildcardProperty.GetBoolean();
            }

            if (plan.TryGetProperty("baseDomain", out var baseProperty) &&
                baseProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                baseDomain = baseProperty.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }

        return (wildcard, baseDomain);
    }

    private sealed record ProviderStatus(
        string ProviderType,
        bool Enabled,
        string LastTestStatus,
        string LastTestError);

    private sealed record CertificateArtifact(
        DateTimeOffset NotBefore,
        DateTimeOffset ExpiresAt,
        string Path);
}
