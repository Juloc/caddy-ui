using System.Globalization;

namespace CaddyUi.Application.Dns;

public static class DnsChallengeTiming
{
    public const string PropagationDelayKey = "propagation_delay";
    public const string PropagationTimeoutKey = "propagation_timeout";

    private const double MaximumSeconds = 86_400;

    public static string NormalizeDelay(string? value)
    {
        return Normalize(value, "Propagation-Delay", allowZero: true);
    }

    public static string NormalizeTimeout(string? value)
    {
        return Normalize(value, "Propagation-Timeout", allowZero: false);
    }

    private static string Normalize(string? value, string label, bool allowZero)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (long.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var secondsOnly))
        {
            candidate = $"{secondsOnly}s";
        }

        var (number, multiplier) = ReadDurationParts(candidate, label);
        var seconds = number * multiplier;
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > MaximumSeconds)
        {
            throw new ArgumentException($"{label} muss zwischen 0 Sekunden und 24 Stunden liegen.");
        }

        if (!allowZero && seconds <= 0)
        {
            throw new ArgumentException($"{label} muss größer als 0 Sekunden sein.");
        }

        return candidate;
    }

    private static (double Number, double Multiplier) ReadDurationParts(string candidate, string label)
    {
        string numberText;
        double multiplier;
        if (candidate.EndsWith("ms", StringComparison.Ordinal))
        {
            numberText = candidate[..^2];
            multiplier = 0.001;
        }
        else if (candidate.Length > 1)
        {
            numberText = candidate[..^1];
            multiplier = candidate[^1] switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3_600,
                'd' => 86_400,
                _ => -1,
            };
        }
        else
        {
            numberText = string.Empty;
            multiplier = -1;
        }

        if (multiplier < 0 ||
            !double.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            number < 0)
        {
            throw new ArgumentException($"{label} ist ungültig. Verwende zum Beispiel 600s, 10m oder 1h.");
        }

        return (number, multiplier);
    }
}
