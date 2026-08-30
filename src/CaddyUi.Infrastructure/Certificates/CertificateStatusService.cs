using System.Data;
using System.Data.Common;
using System.Globalization;
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
        // The filesystem is read on every request. No certificate inventory is cached.
        var store = CaddyCertificateStoreReader.Read(_options.CertificateDirectory);
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
            var domainName = CaddyCertificateStoreReader.NormalizeName(reader.GetString(1));
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

            var wildcard = await BuildStatusAsync(
                "wildcard",
                $"*.{domainName}",
                domainName,
                plan.Wildcard,
                provider,
                store,
                appliedNames,
                lifecycleLogs,
                cancellationToken);
            var baseDomain = await BuildStatusAsync(
                "base",
                domainName,
                domainName,
                plan.BaseDomain,
                provider: null,
                store,
                appliedNames,
                lifecycleLogs,
                cancellationToken);

            var certificates = new List<CertificateStatusItem> { wildcard, baseDomain };
            var additionalNames = GetAdditionalAppliedCertificateNames(domainName, appliedNames);
            for (var index = 0; index < additionalNames.Count; index++)
            {
                var name = additionalNames[index];
                var isWildcard = name.StartsWith("*.", StringComparison.Ordinal);
                var status = await BuildStatusAsync(
                    isWildcard ? "wildcard" : "individual",
                    name,
                    isWildcard ? name[2..] : name,
                    requested: true,
                    isWildcard ? provider : null,
                    store,
                    appliedNames,
                    lifecycleLogs,
                    cancellationToken);

                // Kind is also used as a DOM id suffix. Route certificates need a unique
                // presentation kind while retaining the wildcard/individual lifecycle above.
                certificates.Add(status with { Kind = $"route-{index}" });
            }

            result[domainId] = new DomainCertificateStatus(
                domainId,
                domainName,
                certificates);
        }

        return result;
    }

    private async Task<CertificateStatusItem> BuildStatusAsync(
        string kind,
        string name,
        string domainName,
        bool requested,
        ProviderStatus? provider,
        CaddyCertificateStoreSnapshot store,
        IReadOnlySet<string> appliedNames,
        IReadOnlyDictionary<string, CaddyCertificateLogState> lifecycleLogs,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var artifact = store.FindLatestValid(name, now);
        var historicalArtifact = artifact ?? store.FindLatestHistorical(name);
        lifecycleLogs.TryGetValue(name, out var logState);
        var applied = appliedNames.Contains(name);
        var providerProblem = kind == "wildcard" ? ProviderProblem(provider) : null;
        var lifecycle = BuildLifecycle(
            kind,
            name,
            domainName,
            requested,
            provider,
            applied,
            artifact,
            historicalArtifact,
            logState,
            providerProblem);

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
                lifecycle);
        }

        // A currently valid certificate from Caddy's storage is authoritative.
        // Old failures, retries, and provider diagnostics must not override it.
        if (artifact is not null)
        {
            var days = DaysRemaining(artifact, now);
            var renewalWindowStartsAt = RenewalWindowStartsAt(artifact);
            var renewing = IsRelevantActiveRenewal(logState, artifact);
            var detail = renewing
                ? $"Aktuelles Zertifikat ist bis {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC gültig. Caddy bearbeitet parallel eine Erneuerung."
                : $"Gültig von {artifact.NotBefore:dd.MM.yyyy HH:mm} UTC bis {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC ({days} Tage).";
            return CreateItem(
                kind,
                name,
                requested,
                "active",
                "Aktiv",
                detail,
                artifact,
                lifecycle,
                renewalWindowStartsAt);
        }

        var servedCertificateIsValid = applied &&
            await CaddyServedCertificateProbe.HasValidCertificateAsync(
                name,
                domainName,
                cancellationToken);
        if (servedCertificateIsValid)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "unreadable",
                "Status konnte nicht gelesen werden",
                "HTTPS liefert ein gültiges Zertifikat, aber die aktuelle Zertifikatsdatei wurde im gemounteten Caddy-Speicher nicht gefunden. Die Anzeige behauptet deshalb nicht, dass das Zertifikat abgelaufen ist.",
                artifact: null,
                lifecycle);
        }

        if (!store.DirectoryAvailable || !store.ReadSucceeded)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "unreadable",
                "Status konnte nicht gelesen werden",
                "Der Caddy-Zertifikatsspeicher ist nicht vorhanden oder nicht lesbar. Logs werden nur als Verlauf angezeigt und ersetzen den Zertifikatsstatus nicht.",
                artifact: null,
                lifecycle);
        }

        var nextAttemptScheduled =
            logState?.NextAttemptAt is not null && logState.NextAttemptAt > now;
        var latestFailed = logState?.CurrentState is "failed" or "retry-scheduled";

        if (providerProblem is not null)
        {
            return CreateItem(
                kind,
                name,
                requested,
                "blocked",
                historicalArtifact?.ExpiresAt <= now
                    ? "Erneuerung blockiert"
                    : "Beschaffung blockiert",
                providerProblem,
                historicalArtifact,
                lifecycle);
        }

        if (logState?.Active == true)
        {
            return CreateItem(
                kind,
                name,
                requested,
                historicalArtifact?.ExpiresAt <= now ? "renewing" : "obtaining",
                historicalArtifact?.ExpiresAt <= now ? "Erneuerung läuft" : "Beschaffung läuft",
                "Caddy bearbeitet aktuell einen Zertifikatsversuch. Der Status bleibt getrennt von den Logdaten, bis eine aktuelle Zertifikatsdatei gelesen werden kann.",
                historicalArtifact,
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
                $"Der nächste Versuch ist laut Caddy-Log für {logState!.NextAttemptAt:dd.MM.yyyy HH:mm:ss} UTC geplant.",
                historicalArtifact,
                lifecycle);
        }

        if (historicalArtifact is not null && historicalArtifact.ExpiresAt <= now)
        {
            if (latestFailed || logState?.ConsecutiveFailures > 0)
            {
                return CreateItem(
                    kind,
                    name,
                    requested,
                    "renewal-failed",
                    "Erneuerung fehlgeschlagen",
                    RenewalFailureDetail(historicalArtifact, logState),
                    historicalArtifact,
                    lifecycle);
            }

            return CreateItem(
                kind,
                name,
                requested,
                applied ? "renewal-pending" : "expired",
                applied ? "Erneuerung ausstehend" : "Abgelaufen",
                $"Das neueste eindeutig zugeordnete gespeicherte Zertifikat ist seit {historicalArtifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen.",
                historicalArtifact,
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
                artifact: null,
                lifecycle);
        }

        if (logState?.LastSuccessAt is not null &&
            logState.LastSuccessAt >= now.AddMinutes(-15))
        {
            return CreateItem(
                kind,
                name,
                requested,
                "verifying",
                "Speicher wird geprüft",
                $"Caddy meldete am {logState.LastSuccessAt:dd.MM.yyyy HH:mm:ss} UTC einen erfolgreichen Vorgang. Die aktuelle Zertifikatsdatei ist noch nicht lesbar.",
                artifact: null,
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
                "Der Name ist im aktiven Caddy-Stand enthalten, aber im Zertifikatsspeicher ist noch kein aktuelles Zertifikat nachweisbar.",
                artifact: null,
                lifecycle);
        }

        return CreateItem(
            kind,
            name,
            requested,
            "draft",
            "Bereit für Apply",
            kind == "wildcard" && provider?.LastTestStatus == "untested"
                ? "Konfiguration ist vollständig, der Provider wurde aber noch nicht getestet. Danach Vorschau und Apply ausführen."
                : "Noch nicht im aktiven Caddy-Stand. Vorschau prüfen und anschließend Apply ausführen.",
            artifact: null,
            lifecycle);
    }

    private CertificateLifecycleStatus BuildLifecycle(
        string kind,
        string name,
        string domainName,
        bool requested,
        ProviderStatus? provider,
        bool applied,
        CaddyCertificateArtifact? currentArtifact,
        CaddyCertificateArtifact? historicalArtifact,
        CaddyCertificateLogState? logState,
        string? providerProblem)
    {
        var now = DateTimeOffset.UtcNow;
        var logIsNewerThanCertificate = LogIsNewerThanCertificate(logState, currentArtifact);
        var lifecycleState = "idle";
        var lifecycleLabel = requested ? "Noch kein Versuch erkannt" : "Nicht angefordert";

        if (logIsNewerThanCertificate && logState?.Active == true)
        {
            lifecycleState = "in-progress";
            lifecycleLabel = logState.CurrentState switch
            {
                "challenging" => "DNS-01-Challenge läuft",
                "propagating" => "DNS-Propagation wird geprüft",
                _ => currentArtifact is null && historicalArtifact?.ExpiresAt <= now
                    ? "Erneuerung läuft"
                    : "Beschaffung läuft",
            };
        }
        else if (currentArtifact is not null)
        {
            lifecycleState = "managed";
            lifecycleLabel = "Automatisch durch Caddy verwaltet";
        }
        else if (logState?.NextAttemptAt > now)
        {
            lifecycleState = "retry-scheduled";
            lifecycleLabel = "Neuer Versuch geplant";
        }
        else if (logState?.CurrentState == "failed" || logState?.ConsecutiveFailures > 0)
        {
            lifecycleState = "failed";
            lifecycleLabel = historicalArtifact is null
                ? "Beschaffung fehlgeschlagen"
                : "Erneuerung fehlgeschlagen";
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

        var ttlSeconds = ReadDurationSetting(
            provider?.ConfigJson,
            "ttl",
            "ttl_seconds",
            "dns_ttl",
            "dnsTtl");
        var propagationTimeoutSeconds = ReadDurationSetting(
            provider?.ConfigJson,
            "propagation_timeout",
            "propagationTimeout",
            "dns_propagation_timeout");
        var recentAttempts = logState?.RecentEvents
            .Select(item => new CertificateAttemptItem(
                item.Timestamp,
                item.State,
                item.Label,
                item.Detail,
                item.Attempt,
                item.NextAttemptAt))
            .ToArray() ?? [];

        var latestFailureAt = logState?.RecentEvents
            .Where(item => item.State is "failed" or "retry-scheduled")
            .Select(item => (DateTimeOffset?)item.Timestamp)
            .Max();
        var certificateSupersedesFailure =
            currentArtifact is not null &&
            latestFailureAt is not null &&
            currentArtifact.NotBefore >= latestFailureAt;

        return new CertificateLifecycleStatus(
            lifecycleState,
            lifecycleLabel,
            applied,
            now,
            logState?.LastAttemptAt,
            logState?.LastSuccessAt,
            logState?.NextAttemptAt,
            logState?.AttemptCount ?? 0,
            certificateSupersedesFailure ? 0 : logState?.ConsecutiveFailures ?? 0,
            certificateSupersedesFailure ? string.Empty : logState?.LastError ?? string.Empty,
            provider?.ProviderType ?? string.Empty,
            provider?.LastTestStatus ?? string.Empty,
            provider?.LastTestedAt,
            ttlSeconds,
            propagationTimeoutSeconds,
            kind == "wildcard" ? $"_acme-challenge.{domainName}" : string.Empty,
            BuildTips(
                kind,
                name,
                requested,
                applied,
                currentArtifact,
                historicalArtifact,
                logState,
                provider,
                providerProblem,
                ttlSeconds,
                propagationTimeoutSeconds),
            recentAttempts);
    }

    private static IReadOnlyList<string> BuildTips(
        string kind,
        string name,
        bool requested,
        bool applied,
        CaddyCertificateArtifact? currentArtifact,
        CaddyCertificateArtifact? historicalArtifact,
        CaddyCertificateLogState? logState,
        ProviderStatus? provider,
        string? providerProblem,
        int? ttlSeconds,
        int? propagationTimeoutSeconds)
    {
        var tips = new List<string>();
        if (!requested)
        {
            tips.Add("Für diesen Namen ist aktuell kein Zertifikat geplant.");
            return tips;
        }

        if (currentArtifact is not null)
        {
            tips.Add("Der angezeigte Status und das Ablaufdatum stammen direkt aus der neuesten aktuell gültigen Zertifikatsdatei im Caddy-Speicher.");
        }

        if (providerProblem is not null && currentArtifact is null)
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
            tips.Add(ttlSeconds is null
                ? "Im Providerprofil ist keine DNS-TTL hinterlegt."
                : $"Konfigurierte DNS-TTL: {FormatDuration(ttlSeconds.Value)}.");
            tips.Add(propagationTimeoutSeconds is null
                ? "Kein eigener Propagation-Timeout im Providerprofil erkannt."
                : $"Konfigurierter Propagation-Timeout: {FormatDuration(propagationTimeoutSeconds.Value)}.");
            if (provider?.LastTestStatus == "untested")
            {
                tips.Add("DNS-Provider zuerst in der UI testen.");
            }
        }

        if (currentArtifact is null &&
            historicalArtifact?.ExpiresAt <= DateTimeOffset.UtcNow &&
            logState is null)
        {
            tips.Add("Nur ein abgelaufenes historisches Zertifikat wurde gefunden. Caddy-Konfiguration, Speicher-Mount und aktuelle Ausstellung prüfen.");
        }

        if (logState?.LastError.Contains("propagation", StringComparison.OrdinalIgnoreCase) == true ||
            logState?.LastError.Contains("dns", StringComparison.OrdinalIgnoreCase) == true)
        {
            tips.Add("TXT-Record direkt gegen die autoritativen Nameserver prüfen.");
        }

        if (logState is null)
        {
            tips.Add("Keine passenden Lifecycle-Ereignisse in den verfügbaren Caddy-Logs gefunden.");
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
        CaddyCertificateArtifact? artifact,
        CertificateLifecycleStatus lifecycle,
        DateTimeOffset? renewalWindowStartsAt = null)
    {
        return new CertificateStatusItem(
            kind,
            name,
            requested,
            state,
            label,
            detail,
            artifact?.NotBefore,
            artifact?.ExpiresAt,
            artifact is null ? null : DaysRemaining(artifact, DateTimeOffset.UtcNow),
            renewalWindowStartsAt ??
                (artifact is null ? null : RenewalWindowStartsAt(artifact)),
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

    private static bool IsRelevantActiveRenewal(
        CaddyCertificateLogState? logState,
        CaddyCertificateArtifact artifact)
    {
        return logState?.Active == true &&
               logState.RecentEvents.Any(item =>
                   item.Operation == "renewal" &&
                   item.Timestamp >= artifact.NotBefore);
    }

    private static bool LogIsNewerThanCertificate(
        CaddyCertificateLogState? logState,
        CaddyCertificateArtifact? artifact)
    {
        if (logState is null)
        {
            return false;
        }

        return artifact is null ||
               logState.RecentEvents.Any(item => item.Timestamp >= artifact.NotBefore);
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

    internal static IReadOnlyList<string> GetAdditionalAppliedCertificateNames(
        string domainName,
        IReadOnlySet<string> appliedNames)
    {
        var root = CaddyCertificateStoreReader.NormalizeName(domainName);
        if (root.Length == 0)
        {
            return [];
        }

        var rootWildcard = $"*.{root}";
        return appliedNames
            .Select(CaddyCertificateStoreReader.NormalizeName)
            .Where(name => name.Length > 0)
            .Where(name => !string.Equals(name, root, StringComparison.OrdinalIgnoreCase))
            .Where(name => !string.Equals(name, rootWildcard, StringComparison.OrdinalIgnoreCase))
            .Where(name =>
            {
                var host = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
                return host.EndsWith($".{root}", StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

                if (value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt32(out var number))
                {
                    return Math.Max(number, 0);
                }

                if (value.ValueKind == JsonValueKind.String &&
                    TryParseSeconds(value.GetString(), out var seconds))
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
        if (int.TryParse(
                candidate,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out seconds))
        {
            seconds = Math.Max(seconds, 0);
            return true;
        }

        if (candidate.EndsWith("ms", StringComparison.Ordinal) &&
            double.TryParse(
                candidate[..^2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var milliseconds))
        {
            seconds = Math.Max((int)Math.Ceiling(milliseconds / 1_000), 0);
            return true;
        }

        var suffix = candidate[^1];
        if (!double.TryParse(
                candidate[..^1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount))
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

    private static DateTimeOffset RenewalWindowStartsAt(
        CaddyCertificateArtifact artifact)
    {
        var lifetime = artifact.ExpiresAt - artifact.NotBefore;
        return artifact.NotBefore.AddTicks(lifetime.Ticks * 2 / 3);
    }

    private static int DaysRemaining(
        CaddyCertificateArtifact artifact,
        DateTimeOffset now)
    {
        return (int)Math.Floor((artifact.ExpiresAt - now).TotalDays);
    }

    private static string RenewalFailureDetail(
        CaddyCertificateArtifact artifact,
        CaddyCertificateLogState? logState)
    {
        var detail =
            $"Das Zertifikat ist seit {artifact.ExpiresAt:dd.MM.yyyy HH:mm} UTC abgelaufen.";
        if (logState?.LastAttemptAt is not null)
        {
            detail =
                $"{detail} Letzter erkannter Versuch: {logState.LastAttemptAt:dd.MM.yyyy HH:mm:ss} UTC.";
        }

        if (!string.IsNullOrWhiteSpace(logState?.LastError))
        {
            detail = $"{detail} {logState.LastError}";
        }

        return detail;
    }

    private static DateTimeOffset ReadTimestamp(
        DbDataReader reader,
        int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime =>
                new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        };
    }

    private static void AddName(ISet<string> names, string? value)
    {
        var candidate = CaddyCertificateStoreReader.NormalizeName(value);
        if (candidate.Length > 0 && candidate.Contains('.', StringComparison.Ordinal))
        {
            names.Add(candidate);
        }
    }

    private sealed record ProviderStatus(
        string ProviderType,
        bool Enabled,
        string ConfigJson,
        DateTimeOffset? LastTestedAt,
        string LastTestStatus,
        string LastTestError);
}