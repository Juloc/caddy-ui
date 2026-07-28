using System.Globalization;
using CaddyUi.Domain.Security;

namespace CaddyUi.Application.Security;

public sealed class ClientRiskEngine
{
    public const string CurrentVersion = "risk-v1";

    public ClientRiskAssessment Assess(ClientRiskSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.SampleEndedAt < sample.SampleStartedAt)
        {
            throw new ArgumentException(
                "The risk sample end must not precede its start.",
                nameof(sample));
        }

        var reasons = new List<ClientRiskReason>();
        AddKnownSignature(sample, reasons);
        AddUserAgentEvidence(sample, reasons);
        AddRateEvidence(sample, reasons);
        AddRegularityEvidence(sample, reasons);
        AddPathEvidence(sample, reasons);
        AddErrorEvidence(sample, reasons);
        AddMethodAndHostEvidence(sample, reasons);

        var score = Math.Clamp(reasons.Sum(reason => reason.Weight), 0, 100);
        var risk = score switch
        {
            >= 70 => ClientRiskLevel.High,
            >= 40 => ClientRiskLevel.Medium,
            _ => ClientRiskLevel.Low,
        };
        var classification = score switch
        {
            >= 70 => "bot",
            >= 40 => "suspicious",
            _ when string.Equals(sample.ExistingActorType, "human", StringComparison.OrdinalIgnoreCase) => "human",
            _ => "unknown",
        };

        return new ClientRiskAssessment(
            classification,
            score,
            risk,
            CurrentVersion,
            sample.SampleStartedAt,
            sample.SampleEndedAt,
            reasons);
    }

    private static void AddKnownSignature(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        if (sample.KnownBotSignature ||
            ContainsAutomationToken(sample.UserAgent))
        {
            reasons.Add(
                Reason(
                    "known-automation-signature",
                    "Bekannte Automatisierungs- oder Bot-Signatur erkannt.",
                    55,
                    ("userAgent", Truncate(sample.UserAgent, 120))));
        }
    }

    private static void AddUserAgentEvidence(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        if (string.IsNullOrWhiteSpace(sample.UserAgent))
        {
            reasons.Add(
                Reason(
                    "missing-user-agent",
                    "User-Agent fehlt.",
                    20));
        }
    }

    private static void AddRateEvidence(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        var seconds = Math.Max(1, sample.Window.TotalSeconds);
        var requestsPerSecond = sample.RequestCount / seconds;
        if (requestsPerSecond >= 5)
        {
            reasons.Add(
                Reason(
                    "very-high-request-rate",
                    "Sehr hohe Requestrate im betrachteten Zeitraum.",
                    35,
                    ("requestsPerSecond", requestsPerSecond.ToString("0.###", CultureInfo.InvariantCulture))));
        }
        else if (requestsPerSecond >= 1)
        {
            reasons.Add(
                Reason(
                    "high-request-rate",
                    "Erhöhte Requestrate im betrachteten Zeitraum.",
                    15,
                    ("requestsPerSecond", requestsPerSecond.ToString("0.###", CultureInfo.InvariantCulture))));
        }
    }

    private static void AddRegularityEvidence(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        if (sample.RequestCount >= 20 && sample.IntervalRegularity >= 0.9)
        {
            reasons.Add(
                Reason(
                    "regular-request-intervals",
                    "Requestabstände sind auffällig gleichförmig.",
                    20,
                    ("regularity", sample.IntervalRegularity.ToString("0.###", CultureInfo.InvariantCulture))));
        }
    }

    private static void AddPathEvidence(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        if (sample.ScannerPathCount > 0)
        {
            reasons.Add(
                Reason(
                    "scanner-paths",
                    "Typische Scanner- oder Exploit-Pfade wurden aufgerufen.",
                    40,
                    ("count", sample.ScannerPathCount.ToString(CultureInfo.InvariantCulture))));
        }

        if (sample.DistinctPathCount >= 50)
        {
            reasons.Add(
                Reason(
                    "wide-path-scan",
                    "Ungewöhnlich viele unterschiedliche Pfade wurden angefragt.",
                    15,
                    ("distinctPaths", sample.DistinctPathCount.ToString(CultureInfo.InvariantCulture))));
        }
    }

    private static void AddErrorEvidence(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        if (sample.NotFoundRatio >= 0.5)
        {
            reasons.Add(
                Reason(
                    "high-404-ratio",
                    "Mindestens die Hälfte der Requests endete mit 404.",
                    30,
                    ("ratio", sample.NotFoundRatio.ToString("0.###", CultureInfo.InvariantCulture))));
        }
        else if (sample.NotFoundRatio >= 0.2)
        {
            reasons.Add(
                Reason(
                    "elevated-404-ratio",
                    "Der 404-Anteil ist erhöht.",
                    15,
                    ("ratio", sample.NotFoundRatio.ToString("0.###", CultureInfo.InvariantCulture))));
        }

        if (sample.AuthenticationFailureRatio >= 0.25)
        {
            reasons.Add(
                Reason(
                    "authentication-failures",
                    "Viele Requests endeten mit 401 oder 403.",
                    25,
                    ("ratio", sample.AuthenticationFailureRatio.ToString("0.###", CultureInfo.InvariantCulture))));
        }
    }

    private static void AddMethodAndHostEvidence(
        ClientRiskSample sample,
        ICollection<ClientRiskReason> reasons)
    {
        if (sample.UnsafeMethodCount > 0)
        {
            reasons.Add(
                Reason(
                    "unusual-methods",
                    "Ungewöhnliche HTTP-Methoden wurden verwendet.",
                    15,
                    ("count", sample.UnsafeMethodCount.ToString(CultureInfo.InvariantCulture))));
        }

        if (sample.HostCount >= 6)
        {
            reasons.Add(
                Reason(
                    "many-hosts",
                    "Der Client wechselte in kurzer Zeit zwischen vielen Hosts.",
                    10,
                    ("hosts", sample.HostCount.ToString(CultureInfo.InvariantCulture))));
        }
    }

    private static ClientRiskReason Reason(
        string code,
        string message,
        int weight,
        params (string Key, string Value)[] evidence)
    {
        return new ClientRiskReason(
            code,
            message,
            weight,
            evidence.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    }

    private static bool ContainsAutomationToken(string value)
    {
        var tokens = new[]
        {
            "bot",
            "crawler",
            "spider",
            "scanner",
            "python-requests",
            "go-http-client",
            "curl/",
            "wget/",
            "headless",
            "selenium",
            "playwright",
        };
        return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
