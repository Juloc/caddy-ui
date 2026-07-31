using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Infrastructure.Certificates;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class DomainsModel : LocalizedPageModel
{
    private readonly DomainProviderStore _store;
    private readonly CertificateStatusService _certificateStatusService;
    private readonly CaddyApplyService _applyService;
    private readonly ICaddyCommandRunner _commandRunner;
    private readonly ILogger<DomainsModel> _logger;

    public DomainsModel(
        DomainProviderStore store,
        CertificateStatusService certificateStatusService,
        CaddyApplyService applyService,
        ICaddyCommandRunner commandRunner,
        ILogger<DomainsModel> logger)
    {
        _store = store;
        _certificateStatusService = certificateStatusService;
        _applyService = applyService;
        _commandRunner = commandRunner;
        _logger = logger;
    }

    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } = Array.Empty<ManagedDomainRecord>();

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();

    public IReadOnlyDictionary<Guid, DomainCertificateStatus> CertificateStatuses { get; private set; } =
        new Dictionary<Guid, DomainCertificateStatus>();

    public IReadOnlyDictionary<Guid, DnsChallengeTimingView> DnsChallengeTimings { get; private set; } =
        new Dictionary<Guid, DnsChallengeTimingView>();

    public bool HasActiveCertificateWork => CertificateStatuses.Values
        .SelectMany(status => status.Certificates)
        .Any(certificate => certificate.Lifecycle.State == "in-progress");

    [BindProperty]
    public DomainInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            await _store.CreateDomainWithCertificatePlanAsync(
                Input.Name,
                Input.DisplayName,
                Input.DnsProviderId,
                Input.RequestWildcardCertificate,
                Input.RequestBaseCertificate,
                Input.MakeDefault,
                HttpContext.RequestAborted);
            TempData["Message"] = "Domain wurde als Entwurf angelegt. Zertifikate werden erst nach Vorschau und Apply beschafft.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDefaultAsync(Guid domainId)
    {
        await _store.SetDefaultDomainAsync(domainId, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryCertificateAsync(Guid domainId, string certificateName)
    {
        try
        {
            var statuses = await _certificateStatusService.GetDomainStatusesAsync(HttpContext.RequestAborted);
            if (!statuses.TryGetValue(domainId, out var domainStatus))
            {
                throw new InvalidOperationException(Text("The selected domain no longer exists."));
            }

            var certificate = domainStatus.Certificates.FirstOrDefault(item =>
                string.Equals(item.Name, certificateName, StringComparison.OrdinalIgnoreCase));
            if (certificate is null || !certificate.Requested)
            {
                throw new InvalidOperationException("Für diesen Namen ist kein Zertifikat angefordert.");
            }

            if (!certificate.Lifecycle.Applied)
            {
                throw new InvalidOperationException(
                    "Das Zertifikat ist nicht im aktiven Caddy-Stand. Zuerst Vorschau erstellen und Apply ausführen.");
            }

            if (certificate.Lifecycle.State == "in-progress")
            {
                throw new InvalidOperationException("Für dieses Zertifikat läuft bereits ein Caddy-Versuch.");
            }

            var options = _applyService.Options;
            if (options.WriteMode != RouteWriteMode.Active)
            {
                throw new InvalidOperationException("Force-Retry ist nur im aktiven Routing-Modus verfügbar.");
            }

            if (!Path.IsPathFullyQualified(options.RootConfigPath) || !System.IO.File.Exists(options.RootConfigPath))
            {
                throw new InvalidOperationException("Die aktive Caddy-Hauptkonfiguration ist nicht verfügbar.");
            }

            var timeout = TimeSpan.FromSeconds(options.CommandTimeoutSeconds);
            var validation = await _commandRunner.RunAsync(
                ["validate", "--config", options.RootConfigPath, "--adapter", "caddyfile"],
                timeout,
                HttpContext.RequestAborted);
            EnsureCommandSucceeded(validation, "Die aktive Caddy-Konfiguration ist ungültig.");

            var reload = await _commandRunner.RunAsync(
                ["reload", "--force", "--config", options.RootConfigPath, "--adapter", "caddyfile"],
                timeout,
                HttpContext.RequestAborted);
            EnsureCommandSucceeded(reload, "Caddy konnte nicht für einen neuen Zertifikatsversuch geladen werden.");

            _logger.LogWarning(
                "Administrator {Username} triggered a forced Caddy reload for certificate {CertificateName} on domain {DomainId}",
                User.Identity?.Name ?? "unknown",
                certificate.Name,
                domainId);
            TempData["Message"] =
                $"Force-Retry für {certificate.Name} wurde angestoßen. Caddy hat die aktive Konfiguration validiert und ohne Container-Neustart erzwungen neu geladen.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
        var allProviders = await _store.ListProvidersAsync(HttpContext.RequestAborted);
        Providers = allProviders
            .Where(provider => provider.Enabled)
            .ToArray();
        CertificateStatuses = await _certificateStatusService.GetDomainStatusesAsync(HttpContext.RequestAborted);
        DnsChallengeTimings = Domains
            .Where(domain => domain.DnsProviderId is not null)
            .Select(domain => new
            {
                domain.Id,
                Provider = allProviders.FirstOrDefault(provider => provider.Id == domain.DnsProviderId),
            })
            .Where(item => item.Provider is not null)
            .ToDictionary(item => item.Id, item => ReadDnsChallengeTiming(item.Provider!.ConfigJson));
    }

    public static string StatusClass(string state)
    {
        return state switch
        {
            "active" or "succeeded" => "status-badge--ok",
            "renewal-due" or "requested" or "draft" or "renewing" or "obtaining" or
                "retry-scheduled" or "renewal-pending" or "verifying" => "status-badge--warning",
            "blocked" or "expired" or "renewal-failed" or "acquisition-failed" => "status-badge--danger",
            _ => "status-badge--neutral",
        };
    }

    public static string FormatUtc(DateTimeOffset? value)
    {
        return value is null
            ? "Nicht bekannt"
            : value.Value.ToUniversalTime().ToString("dd.MM.yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    }

    public static string FormatIso(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static string FormatDuration(int? seconds)
    {
        if (seconds is null)
        {
            return "Nicht hinterlegt";
        }

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

    public static string ProviderTestLabel(string status)
    {
        return status switch
        {
            "passed" or "success" or "succeeded" or "ok" => "Erfolgreich",
            "failed" => "Fehlgeschlagen",
            "untested" => "Noch nicht getestet",
            _ when string.IsNullOrWhiteSpace(status) => "Nicht bekannt",
            _ => status,
        };
    }

    public static IReadOnlyList<CertificateAttemptGroup> GroupAttempts(CertificateLifecycleStatus lifecycle)
    {
        var chronological = lifecycle.RecentAttempts
            .OrderBy(item => item.Timestamp)
            .ToArray();
        if (chronological.Length == 0)
        {
            return Array.Empty<CertificateAttemptGroup>();
        }

        var builders = new List<AttemptGroupBuilder>();
        AttemptGroupBuilder? current = null;
        var nextInferredNumber = 1;
        foreach (var item in chronological)
        {
            var startsNewGroup = current is null ||
                                 item.Attempt is int explicitAttempt && explicitAttempt != current.Number ||
                                 item.State == "started" && current.Events.Count > 0 ||
                                 current.IsClosed && item.State != "retry-scheduled";
            if (startsNewGroup)
            {
                var number = item.Attempt ?? nextInferredNumber;
                nextInferredNumber = Math.Max(nextInferredNumber, number + 1);
                current = new AttemptGroupBuilder(number);
                builders.Add(current);
            }

            current!.Events.Add(item);
            if (item.State is "succeeded" or "retry-scheduled")
            {
                current.IsClosed = true;
            }
        }

        return builders
            .Select(builder => builder.Build())
            .Reverse()
            .ToArray();
    }

    public static string CurrentAction(CertificateLifecycleStatus lifecycle)
    {
        var state = lifecycle.RecentAttempts.FirstOrDefault()?.State ?? lifecycle.State;
        return state switch
        {
            "started" => "ACME-Vorgang wird initialisiert",
            "challenging" => "DNS-01-TXT-Record wird erstellt",
            "propagating" => "DNS-Verteilung wird abgewartet und geprüft",
            "retry-scheduled" => "Caddy wartet auf den nächsten Versuch",
            "failed" => "Der letzte Versuch ist beendet",
            "succeeded" => "Zertifikat wurde erfolgreich verarbeitet",
            "draft" => "Konfiguration wartet auf Apply",
            "managed" => "Caddy verwaltet das Zertifikat automatisch",
            _ => lifecycle.Label,
        };
    }

    public static string CurrentActionDetail(
        CertificateLifecycleStatus lifecycle,
        DnsChallengeTimingView? timing)
    {
        var state = lifecycle.RecentAttempts.FirstOrDefault()?.State ?? lifecycle.State;
        return state switch
        {
            "started" => "Caddy lädt ACME-Konto, Zertifikatsnamen und den konfigurierten DNS-Provider.",
            "challenging" => $"Caddy erzeugt den Challenge-Wert und schreibt ihn als TXT unter {lifecycle.DnsChallengeName}.",
            "propagating" =>
                $"Vor der ersten Prüfung wartet Caddy {FormatDuration(timing?.PropagationDelaySeconds)}. Danach wird bis zu {FormatDuration(timing?.PropagationTimeoutSeconds ?? lifecycle.PropagationTimeoutSeconds)} auf eine sichtbare autoritative DNS-Antwort geprüft.",
            "retry-scheduled" => "Der vorherige Versuch ist fehlgeschlagen. Caddy verwendet seinen Backoff und startet zum angezeigten Zeitpunkt erneut.",
            "failed" => "Der Fehler steht unten vollständig. Ein Force-Retry validiert und lädt dieselbe aktive Konfiguration mit --force neu.",
            "succeeded" => "Caddy prüft oder speichert das ausgestellte Zertifikat im gemeinsamen Zertifikatsspeicher.",
            _ => "Die Anzeige wird aus aktivem Caddy-Stand, Zertifikatsspeicher und den verfügbaren Caddy-Logs zusammengesetzt.",
        };
    }

    public static int CurrentStep(CertificateLifecycleStatus lifecycle)
    {
        var state = lifecycle.RecentAttempts.FirstOrDefault()?.State ?? lifecycle.State;
        return state switch
        {
            "started" => 1,
            "challenging" => 2,
            "propagating" => 3,
            "failed" or "retry-scheduled" => 3,
            "succeeded" => 4,
            _ => 0,
        };
    }

    public static string StepClass(int step, CertificateLifecycleStatus lifecycle)
    {
        var current = CurrentStep(lifecycle);
        var state = lifecycle.RecentAttempts.FirstOrDefault()?.State ?? lifecycle.State;
        if (state == "succeeded" && step <= 4)
        {
            return "certificate-step--done";
        }

        if (step < current)
        {
            return "certificate-step--done";
        }

        if (step == current && state == "failed")
        {
            return "certificate-step--failed";
        }

        return step == current ? "certificate-step--active" : string.Empty;
    }

    public static DateTimeOffset? EstimatedDnsCheckAt(
        CertificateLifecycleStatus lifecycle,
        DnsChallengeTimingView? timing)
    {
        var latestAttempt = GroupAttempts(lifecycle).FirstOrDefault();
        return latestAttempt is null || timing?.PropagationDelaySeconds is not int delay
            ? null
            : latestAttempt.StartedAt.AddSeconds(delay);
    }

    public static DateTimeOffset? EstimatedDnsDeadlineAt(
        CertificateLifecycleStatus lifecycle,
        DnsChallengeTimingView? timing)
    {
        var checkAt = EstimatedDnsCheckAt(lifecycle, timing);
        var timeout = timing?.PropagationTimeoutSeconds ?? lifecycle.PropagationTimeoutSeconds;
        return checkAt is null || timeout is null ? null : checkAt.Value.AddSeconds(timeout.Value);
    }

    private static DnsChallengeTimingView ReadDnsChallengeTiming(string configJson)
    {
        return new DnsChallengeTimingView(
            ReadDurationSetting(configJson, "propagation_delay", "propagationDelay"),
            ReadDurationSetting(configJson, "propagation_timeout", "propagationTimeout", "dns_propagation_timeout"));
    }

    private static int? ReadDurationSetting(string configJson, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
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

        if (candidate.EndsWith("ms", StringComparison.Ordinal) &&
            double.TryParse(candidate[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            seconds = Math.Max((int)Math.Ceiling(milliseconds / 1_000), 0);
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

    private static void EnsureCommandSucceeded(CaddyCommandResult result, string fallback)
    {
        if (result.Succeeded)
        {
            return;
        }

        var detail = result.TimedOut
            ? "Zeitüberschreitung beim Aufruf von Caddy."
            : string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
        if (detail.Length > 1_000)
        {
            detail = detail[..1_000];
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? fallback : $"{fallback} {detail}");
    }

    public sealed class DomainInput
    {
        [Required]
        [MaxLength(253)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        public Guid? DnsProviderId { get; set; }

        public bool RequestWildcardCertificate { get; set; } = true;

        public bool RequestBaseCertificate { get; set; } = true;

        public bool MakeDefault { get; set; }
    }

    public sealed record DnsChallengeTimingView(
        int? PropagationDelaySeconds,
        int? PropagationTimeoutSeconds);

    public sealed record CertificateAttemptGroup(
        int Number,
        string State,
        string Label,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        DateTimeOffset? NextAttemptAt,
        IReadOnlyList<CertificateAttemptItem> Events);

    private sealed class AttemptGroupBuilder
    {
        public AttemptGroupBuilder(int number)
        {
            Number = number;
        }

        public int Number { get; }

        public bool IsClosed { get; set; }

        public List<CertificateAttemptItem> Events { get; } = [];

        public CertificateAttemptGroup Build()
        {
            var last = Events[^1];
            return new CertificateAttemptGroup(
                Number,
                last.State,
                AttemptLabel(last.State),
                Events[0].Timestamp,
                last.State is "succeeded" or "failed" or "retry-scheduled" ? last.Timestamp : null,
                Events.LastOrDefault(item => item.NextAttemptAt is not null)?.NextAttemptAt,
                Events.ToArray());
        }

        private static string AttemptLabel(string state)
        {
            return state switch
            {
                "succeeded" => "Erfolgreich",
                "failed" => "Fehlgeschlagen",
                "retry-scheduled" => "Fehlgeschlagen, Wiederholung geplant",
                "propagating" => "DNS-Prüfung läuft",
                "challenging" => "TXT-Challenge wird erstellt",
                _ => "Läuft",
            };
        }
    }
}
