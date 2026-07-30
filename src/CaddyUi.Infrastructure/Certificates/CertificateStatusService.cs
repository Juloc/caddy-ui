using System.Data;
using System.Data.Common;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Certificates;

public sealed record CertificateAttemptItem(
    DateTimeOffset Timestamp,
    string State,
    string Label,
    string Detail,
    int? Attempt,
    DateTimeOffset? NextAttemptAt);

public sealed record CertificateLifecycleStatus(
    string State,
    string Label,
    bool Applied,
    DateTimeOffset ObservedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextAttemptAt,
    int AttemptCount,
    int ConsecutiveFailures,
    string LastError,
    string ProviderType,
    string ProviderTestStatus,
    DateTimeOffset? ProviderLastTestedAt,
    int? DnsTtlSeconds,
    int? PropagationTimeoutSeconds,
    string DnsChallengeName,
    IReadOnlyList<string> Tips,
    IReadOnlyList<CertificateAttemptItem> RecentAttempts);

public sealed record CertificateStatusItem(
    string Kind,
    string Name,
    bool Requested,
    string State,
    string Label,
    string Detail,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    int? DaysRemaining,
    DateTimeOffset? RenewalWindowStartsAt,
    CertificateLifecycleStatus Lifecycle);

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
        var lifecycleLogs = CaddyCertificateLogReader.Read(_options.CaddyLogPath);
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
                   providers.config_json::text,
                   providers.last_tested_at,
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
                    reader.IsDBNull(7) ? null : ReadTimestamp(reader, 7),
                    reader.GetString(8),
                    reader.GetString(9));
            var certificates = new[]
            {
                BuildStatus(
                    "wildcard",
                    $"*.{domainName}",
                    domainName,
                    plan.Wildcard,
                    provider,
                    inventory,
                    appliedNames,
                    lifecycleLogs),
                BuildStatus(
                    "base",
                    domainName,
                    domainName,
                    plan.BaseDomain,
                    provider: null,
                    inventory,
                    appliedNames,
                    lifecycleLogs),
            };
            result[domainId] = new DomainCertificateStatus(domainId, domainName, certificates);
        }

        return result;
    }

    private CertificateStatusItem BuildStatus(
        string kind,
        string name,
        string domainName,
        bool requested,
        ProviderStatus? provider,
        IReadOnlyDictionary<string, CertificateArtifact> inventory,
        IReadOnlySet<string> appliedNames,
        IReadOnlyDictionary<string, CaddyCertificateLogState> lifecycleLogs)
    {
        inventory.TryGetValue(name, out var artifact);
        lifecycleLogs.TryGetValue(name, out var logState);
        var applied = appliedNames.Contains(name);
        var providerProblem = kind == "wildcard" ? ProviderProblem(provider) : null;
        var renewalWindowStartsAt = artifact is null ? null : RenewalWindowStartsAt(artifact);
        var lifecycle = BuildLifecycle(
            kind,
            name,
            domainName,
            requested,
            provider,
            applied,
            artifact,
            logState,
            providerProblem,
            renewalWindowStartsAt);

        if (!requested)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "not-requested",
                "Nicht angefordert",
                "Kann später über die Domain-Einstellungen aktiviert werden.",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        var now = DateTimeOffset.UtcNow;
        var nextAttemptScheduled = logState?.NextAttemptAt is not null && logState.NextAttemptAt > now;
        var latestFailed = logState?.CurrentState is "failed" or "retry-scheduled";
        if (providerProblem is not null && (artifact is null || artifact.ExpiresAt <= now || renewalWindowStartsAt <= now))
        {
            var prefix = artifact?.ExpiresAt <= now
                ? $"Das gespeicherte Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen. "
                : string.Empty;
            return CreateItem(
                kind,
                name,
                requested,
                "blocked",
                artifact?.ExpiresAt <= now ? "Erneuerung blockiert" : "Beschaffung blockiert",
                $"{prefix}{providerProblem}",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        if (artifact is not null)
        {
            var days = DaysRemaining(artifact, now);
            if (artifact.ExpiresAt <= now)
            {
                if (logState?.Active == true)
                {
                    return CreateItem(
                        kind,
                        name,
                        requested,
                        "renewing",
                        "Erneuerung läuft",
                        $"Das alte Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen. Caddy bearbeitet aktuell einen Erneuerungsversuch.",
                        artifact,
                        renewalWindowStartsAt,
                        lifecycle);
                }

                if (nextAttemptScheduled)
                {
                    return CreateItem(
                        kind,
                        name,
                        requested,
                        "retry-scheduled",
                        "Neuer Versuch geplant",
                        $"Das Zertifikat ist abgelaufen. Der nächste Versuch ist laut Caddy-Log für {logState!.NextAttemptAt:dd.MM.yyyy HH:mm:ss} UTC geplant.",
                        artifact,
                        renewalWindowStartsAt,
                        lifecycle);
                }

                if (latestFailed || logState?.ConsecutiveFailures > 0)
                {
                    return CreateItem(
                        kind,
                        name,
                        requested,
                        "renewal-failed",
                        "Erneuerung fehlgeschlagen",
                        RenewalFailureDetail(artifact, logState),
                        artifact,
                        renewalWindowStartsAt,
                        lifecycle);
                }

                if (applied)
                {
                    return CreateItem(
                        kind,
                        name,
                        requested,
                        "renewal-pending",
                        "Erneuerung ausstehend",
                        $"Das Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen und weiterhin im aktiven Caddy-Stand. Im verfügbaren Log wurde kein laufender Versuch erkannt.",
                        artifact,
                        renewalWindowStartsAt,
                        lifecycle);
                }

                return CreateItem(
                    kind,
                    name,
                    requested,
                    "expired",
                    "Abgelaufen",
                    $"Das gespeicherte Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen und der Zertifikatsname ist nicht im zuletzt angewendeten Stand enthalten.",
                    artifact,
                    renewalWindowStartsAt,
                    lifecycle);
            }

            if (logState?.Active == true && logState.RecentEvents.Any(item => item.Operation == "renewal"))
            {
                return CreateItem(
                    kind,
                    name,
                    requested,
                    "renewing",
                    "Erneuerung läuft",
                    $"Das vorhandene Zertifikat ist noch bis {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC gültig. Caddy bearbeitet aktuell die Erneuerung.",
                    artifact,
                    renewalWindowStartsAt,
                    lifecycle);
            }

            if (renewalWindowStartsAt <= now)
            {
                return CreateItem(
                    kind,
                    name,
                    requested,
                    "renewal-due",
                    "Im Erneuerungsfenster",
                    $"Noch {Math.Max(days, 0)} Tage gültig. Das geschätzte Standard-Erneuerungsfenster begann am {renewalWindowStartsAt:dd.MM.yyyy HH:mm} UTC; ACME ARI kann einen abweichenden Zeitraum vorgeben.",
                    artifact,
                    renewalWindowStartsAt,
                    lifecycle);
            }

            return CreateItem(
                kind,
                name,
                requested,
                "active",
                "Vorhanden",
                $"Gültig bis {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC ({days} Tage). Geschätzter Beginn des Standard-Erneuerungsfensters: {renewalWindowStartsAt:dd.MM.yyyy HH:mm} UTC.",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        if (logState?.Active == true)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "obtaining",
                "Beschaffung läuft",
                "Caddy bearbeitet aktuell einen Zertifikatsversuch. Details und DNS-Phase stehen in der Lifecycle-Ansicht.",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        if (nextAttemptScheduled)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "retry-scheduled",
                "Neuer Versuch geplant",
                $"Noch kein Zertifikat im Speicher. Der nächste Versuch ist laut Caddy-Log für {logState!.NextAttemptAt:dd.MM.yyyy HH:mm:ss} UTC geplant.",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        if (latestFailed || logState?.ConsecutiveFailures > 0)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "acquisition-failed",
                "Beschaffung fehlgeschlagen",
                string.IsNullOrWhiteSpace(logState?.LastError)
                    ? "Der letzte im Caddy-Log erkannte Beschaffungsversuch ist fehlgeschlagen."
                    : logState.LastError,
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        if (logState?.LastSuccessAt is not null && logState.LastSuccessAt >= now.AddMinutes(-15))
        {
            return CreateItem(
                kind,
                name,
                requested,
                "verifying",
                "Speicher wird geprüft",
                $"Caddy meldete am {logState.LastSuccessAt:dd.MM.yyyy HH:mm:ss} UTC einen erfolgreichen Vorgang; die Zertifikatsdatei wurde noch nicht im gemounteten Speicher gefunden.",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        if (applied)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "requested",
                "Angefordert",
                "Der Name ist im aktiven Caddy-Stand enthalten, aber noch nicht im Zertifikatsspeicher nachweisbar. Die Lifecycle-Ansicht zeigt erkannte Versuche und den letzten Fehler.",
                artifact,
                renewalWindowStartsAt,
                lifecycle);
        }

        var detail = kind == "wildcard" && provider?.LastTestStatus == "untested"
            ? "Konfiguration ist vollständig, der Provider wurde aber noch nicht getestet. Danach Vorschau und Apply ausführen."
            : "Noch nicht im aktiven Caddy-Stand. Vorschau prüfen und anschließend Apply ausführen.";
        return CreateItem(
            kind,
            name,
            requested,
            "draft",
            "Bereit für Apply",
            detail,
            artifact,
            renewalWindowStartsAt,
            lifecycle);
    }

    private CertificateLifecycleStatus BuildLifecycle(
        string kind,
        string name,
        string domainName,
        bool requested,
        ProviderStatus? provider,
        bool applied,
        CertificateArtifact? artifact,
        CaddyCertificateLogState? logState,
        string? providerProblem,
        DateTimeOffset? renewalWindowStartsAt)
    {
        var now = DateTimeOffset.UtcNow;
        var lifecycleState = "idle";
        var lifecycleLabel = requested ? "Noch kein Versuch erkannt" : "Nicht angefordert";
        if (logState?.Active == true)
        {
            lifecycleState = "in-progress";
            lifecycleLabel = logState.CurrentState switch
            {
                "challenging" => "DNS-01-Challenge läuft",
                "propagating" => "DNS-Propagation wird geprüft",
                _ => artifact?.ExpiresAt <= now ? "Erneuerung läuft" : "Beschaffung läuft",
            };
        }
        else if (logState?.NextAttemptAt > now)
        {
            lifecycleState = "retry-scheduled";
            lifecycleLabel = "Neuer Versuch geplant";
        }
        else if (logState?.CurrentState == "failed" || logState?.ConsecutiveFailures > 0)
        {
            lifecycleState = "failed";
            lifecycleLabel = artifact is null ? "Beschaffung fehlgeschlagen" : "Erneuerung fehlgeschlagen";
        }
        else if (logState?.CurrentState == "succeeded")
        {
            lifecycleState = "succeeded";
            lifecycleLabel = "Letzter Vorgang erfolgreich";
        }
        else if (providerProblem is not null)
        {
            lifecycleState = "blocked";
            lifecycleLabel = "Durch Konfiguration blockiert";
        }
        else if (applied)
        {
            lifecycleState = "managed";
            lifecycleLabel = "Automatisch durch Caddy verwaltet";
        }
        else if (requested)
        {
            lifecycleState = "draft";
            lifecycleLabel = "Wartet auf Apply";
        }

        var ttlSeconds = ReadDurationSetting(provider?.ConfigJson, "ttl", "ttl_seconds", "dns_ttl", "dnsTtl");
        var propagationTimeoutSeconds = ReadDurationSetting(
            provider?.ConfigJson,
            "propagation_timeout",
            "propagationTimeout",
            "dns_propagation_timeout");
        var tips = BuildTips(
            kind,
            name,
            requested,
            applied,
            artifact,
            logState,
            provider,
            providerProblem,
            ttlSeconds,
            propagationTimeoutSeconds,
            renewalWindowStartsAt);
        var recentAttempts = logState?.RecentEvents
            .Select(item => new CertificateAttemptItem(
                item.Timestamp,
                item.State,
                item.Label,
                item.Detail,
                item.Attempt,
                item.NextAttemptAt))
            .ToArray() ?? [];
        return new CertificateLifecycleStatus(
            lifecycleState,
            lifecycleLabel,
            applied,
            now,
            logState?.LastAttemptAt,
            logState?.LastSuccessAt,
            logState?.NextAttemptAt,
            logState?.AttemptCount ?? 0,
            logState?.ConsecutiveFailures ?? 0,
            logState?.LastError ?? string.Empty,
            provider?.ProviderType ?? string.Empty,
            provider?.LastTestStatus ?? string.Empty,
            provider?.LastTestedAt,
            ttlSeconds,
            propagationTimeoutSeconds,
            kind == "wildcard" ? $"_acme-challenge.{domainName}" : string.Empty,
            tips,
            recentAttempts);
    }

    private static IReadOnlyList<string> BuildTips(
        string kind,
        string name,
        bool requested,
        bool applied,
        CertificateArtifact? artifact,
        CaddyCertificateLogState? logState,
        ProviderStatus? provider,
        string? providerProblem,
        int? ttlSeconds,
        int? propagationTimeoutSeconds,
        DateTimeOffset? renewalWindowStartsAt)
    {
        var tips = new List<string>();
        if (!requested)
        {
            tips.Add("Für diesen Namen ist aktuell kein Zertifikat geplant.");
            return tips;
        }

        if (providerProblem is not null)
        {
            tips.Add(providerProblem);
        }

        if (!applied)
        {
            tips.Add("Die Domain ist noch nicht im zuletzt angewendeten Caddy-Stand. Erst Preview validieren und Apply ausführen.");
        }

        if (kind == "wildcard")
        {
            tips.Add($"Für DNS-01 muss der TXT-Record _acme-challenge.{name[2..]} auf den autoritativen Nameservern sichtbar sein.");
            if (ttlSeconds is null)
            {
                tips.Add("Im Providerprofil ist keine DNS-TTL hinterlegt. Für die Diagnose den tatsächlichen TXT-TTL beim autoritativen Nameserver prüfen.");
            }
            else
            {
                tips.Add($"Konfigurierte DNS-TTL: {FormatDuration(ttlSeconds.Value)}. Resolver können alte Antworten ungefähr bis zum Ablauf dieser TTL zwischenspeichern.");
            }

            if (propagationTimeoutSeconds is null)
            {
                tips.Add("Kein eigener Propagation-Timeout im Providerprofil erkannt; Caddys Standardprüfung kann deshalb maßgeblich sein.");
            }
            else
            {
                tips.Add($"Konfigurierter Propagation-Timeout: {FormatDuration(propagationTimeoutSeconds.Value)}.");
            }

            if (provider?.LastTestStatus == "untested")
            {
                tips.Add("DNS-Provider zuerst in der UI testen, damit Credentials und Schreibzugriff geprüft sind.");
            }
        }

        if (artifact?.ExpiresAt <= DateTimeOffset.UtcNow && logState is null)
        {
            tips.Add("Das Zertifikat ist abgelaufen, aber im verfügbaren Caddy-Log wurde kein passender Erneuerungsversuch erkannt. Caddy-Containerstatus, aktive Konfiguration und Log-Mount prüfen.");
        }

        if (logState?.NextAttemptAt is not null &&
            logState.NextAttemptAt <= DateTimeOffset.UtcNow &&
            logState.Active == false)
        {
            tips.Add("Der zuletzt angekündigte Wiederholungszeitpunkt ist bereits verstrichen. Prüfen, ob Caddy seitdem neu gestartet wurde oder weitere Logs außerhalb der sichtbaren Rotation liegen.");
        }

        if (logState?.LastError.Contains("propagation", StringComparison.OrdinalIgnoreCase) == true ||
            logState?.LastError.Contains("dns", StringComparison.OrdinalIgnoreCase) == true)
        {
            tips.Add("TXT-Record direkt gegen die autoritativen Nameserver prüfen; öffentliche Resolver können wegen Cache und TTL abweichen.");
        }

        if (renewalWindowStartsAt is not null && artifact?.ExpiresAt > DateTimeOffset.UtcNow)
        {
            tips.Add($"Geschätztes Standard-Erneuerungsfenster ab {renewalWindowStartsAt:dd.MM.yyyy HH:mm} UTC. ACME ARI oder eine eigene Caddy-Einstellung kann den tatsächlichen Zeitraum ändern.");
        }

        if (logState is null)
        {
            tips.Add("Keine passenden Lifecycle-Ereignisse in den letzten verfügbaren Caddy-Logdateien gefunden; die Versuchszahl ist dann unbekannt, nicht null.");
        }

        return tips.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static CertificateStatusItem CreateItem(
        string kind,
        string name,
        bool requested,
        string state,
        string label,
        string detail,
        CertificateArtifact? artifact,
        DateTimeOffset? renewalWindowStartsAt,
        CertificateLifecycleStatus lifecycle)
    {
        var days = artifact is null ? null : DaysRemaining(artifact, DateTimeOffset.UtcNow);
        return new CertificateStatusItem(
            kind,
            name,
            requested,
            state,
            label,
            detail,
            artifact?.NotBefore,
            artifact?.ExpiresAt,
            days,
            renewalWindowStartsAt,
            lifecycle);
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

    private static int? ReadDurationSetting(string? json, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var key in keys)
            {
                if (!document.RootElement.TryGetProperty(key, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                {
                    return Math.Max(number, 0);
                }

                if (value.ValueKind == JsonValueKind.String && TryParseSeconds(value.GetString(), out var seconds))
                {
                    return seconds;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool TryParseSeconds(string? value, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            seconds = Math.Max(seconds, 0);
            return true;
        }

        var suffix = candidate[^1];
        if (!double.TryParse(candidate[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        var calculated = suffix switch
        {
            's' => amount,
            'm' => amount * 60,
            'h' => amount * 3_600,
            'd' => amount * 86_400,
            _ => -1,
        };
        if (calculated < 0 || calculated > int.MaxValue)
        {
            return false;
        }

        seconds = (int)Math.Round(calculated, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds > 0 && seconds % 86_400 == 0)
        {
            return $"{seconds / 86_400} d";
        }

        if (seconds > 0 && seconds % 3_600 == 0)
        {
            return $"{seconds / 3_600} h";
        }

        if (seconds > 0 && seconds % 60 == 0)
        {
            return $"{seconds / 60} min";
        }

        return $"{seconds} s";
    }

    private static DateTimeOffset RenewalWindowStartsAt(CertificateArtifact artifact)
    {
        var lifetime = artifact.ExpiresAt - artifact.NotBefore;
        return artifact.NotBefore.AddTicks(lifetime.Ticks * 2 / 3);
    }

    private static int DaysRemaining(CertificateArtifact artifact, DateTimeOffset now)
    {
        return (int)Math.Floor((artifact.ExpiresAt - now).TotalDays);
    }

    private static string RenewalFailureDetail(CertificateArtifact artifact, CaddyCertificateLogState? logState)
    {
        var detail = $"Das Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen.";
        if (logState?.LastAttemptAt is not null)
        {
            detail = $"{detail} Letzter erkannter Versuch: {logState.LastAttemptAt:dd.MM.yyyy HH:mm:ss} UTC.";
        }

        if (!string.IsNullOrWhiteSpace(logState?.LastError))
        {
            detail = $"{detail} {logState.LastError}";
        }

        return detail;
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        };
    }

    private sealed record ProviderStatus(
        string ProviderType,
        bool Enabled,
        string ConfigJson,
        DateTimeOffset? LastTestedAt,
        string LastTestStatus,
        string LastTestError);

    private sealed record CertificateArtifact(
        DateTimeOffset NotBefore,
        DateTimeOffset ExpiresAt,
        string Path);
}
