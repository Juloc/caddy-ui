using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaddyUi.Infrastructure.Certificates;

internal sealed record CaddyCertificateLogEvent(
    string CertificateName,
    DateTimeOffset Timestamp,
    string State,
    string Operation,
    string Label,
    string Detail,
    int? Attempt,
    DateTimeOffset? NextAttemptAt);

internal sealed record CaddyCertificateLogState(
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextAttemptAt,
    int AttemptCount,
    int ConsecutiveFailures,
    string LastError,
    string CurrentState,
    bool Active,
    IReadOnlyList<CaddyCertificateLogEvent> RecentEvents);

internal static partial class CaddyCertificateLogReader
{
    private const int MaximumFiles = 6;
    private const int MaximumLinesPerFile = 4_000;
    private const int MaximumEventsPerCertificate = 8;

    public static IReadOnlyDictionary<string, CaddyCertificateLogState> Read(string logPath)
    {
        var events = new Dictionary<string, List<CaddyCertificateLogEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in CandidateFiles(logPath))
        {
            foreach (var line in ReadTailLines(file, MaximumLinesPerFile))
            {
                foreach (var item in ParseLine(line))
                {
                    if (!events.TryGetValue(item.CertificateName, out var certificateEvents))
                    {
                        certificateEvents = [];
                        events[item.CertificateName] = certificateEvents;
                    }

                    certificateEvents.Add(item);
                }
            }
        }

        return events.ToDictionary(
            pair => pair.Key,
            pair => BuildState(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidateFiles(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(logPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var baseName = Path.GetFileName(fullPath);
            return Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, $"{baseName}*", SearchOption.TopDirectoryOnly))
                .Where(File.Exists)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaximumFiles)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadTailLines(string path, int maximumLines)
    {
        var result = new Queue<string>(maximumLines);
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (result.Count == maximumLines)
                {
                    result.Dequeue();
                }

                result.Enqueue(line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return result;
    }

    private static IReadOnlyList<CaddyCertificateLogEvent> ParseLine(string line)
    {
        var candidate = line.TrimStart();
        if (candidate.Length == 0 || candidate[0] != '{')
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            var message = ReadString(root, "msg");
            var logger = ReadString(root, "logger");
            var error = ReadString(root, "error");
            var searchable = string.Join(' ', logger, message, error).ToLowerInvariant();
            var classification = Classify(searchable);
            if (classification is null)
            {
                return [];
            }

            var timestamp = ReadTimestamp(root) ?? DateTimeOffset.UtcNow;
            var attempt = ReadInteger(root, "attempt");
            var nextAttemptAt = ReadNextAttempt(root, timestamp);
            var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSubjects(root, propertyName: null, subjects);
            if (subjects.Count == 0)
            {
                return [];
            }

            var detail = BuildDetail(message, error, classification.Value.Label, nextAttemptAt);
            return subjects
                .Select(subject => new CaddyCertificateLogEvent(
                    subject,
                    timestamp,
                    classification.Value.State,
                    classification.Value.Operation,
                    classification.Value.Label,
                    detail,
                    attempt,
                    nextAttemptAt))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (string State, string Operation, string Label)? Classify(string text)
    {
        if (!ContainsCertificateContext(text))
        {
            return null;
        }

        var operation = text.Contains("renew", StringComparison.Ordinal) ? "renewal" : "acquisition";
        if (ContainsAny(text,
                "certificate obtained successfully",
                "certificate renewed successfully",
                "successfully obtained certificate",
                "successfully renewed certificate",
                "finished obtaining certificate",
                "finished renewing certificate"))
        {
            return ("succeeded", operation, operation == "renewal" ? "Erneuerung erfolgreich" : "Beschaffung erfolgreich");
        }

        if (ContainsAny(text, "will retry", "retrying in", "next attempt", "retry scheduled"))
        {
            return ("retry-scheduled", operation, "Neuer Versuch geplant");
        }

        if (ContainsAny(text,
                "failed to obtain",
                "failed obtaining",
                "could not get certificate",
                "certificate obtain failed",
                "failed to renew",
                "certificate renewal failed",
                "challenge failed",
                "authorization failed",
                "validating authorization"))
        {
            return ("failed", operation, operation == "renewal" ? "Erneuerung fehlgeschlagen" : "Beschaffung fehlgeschlagen");
        }

        if (ContainsAny(text, "waiting for dns", "dns propagation", "propagation check", "propagated"))
        {
            return ("propagating", operation, "DNS-Propagation wird geprüft");
        }

        if (ContainsAny(text, "presenting challenge", "solving challenge", "dns-01 challenge", "acme challenge"))
        {
            return ("challenging", operation, "DNS-01-Challenge läuft");
        }

        if (ContainsAny(text,
                "obtaining certificate",
                "renewing certificate",
                "attempting certificate renewal",
                "certificate maintenance"))
        {
            return ("started", operation, operation == "renewal" ? "Erneuerung gestartet" : "Beschaffung gestartet");
        }

        return null;
    }

    private static bool ContainsCertificateContext(string text)
    {
        return ContainsAny(
            text,
            "certificate",
            "acme",
            "tls.obtain",
            "tls.renew",
            "dns-01",
            "dns propagation",
            "waiting for dns",
            "challenge",
            "authorization");
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
    }

    private static string BuildDetail(
        string message,
        string error,
        string fallback,
        DateTimeOffset? nextAttemptAt)
    {
        var detail = string.IsNullOrWhiteSpace(error)
            ? message
            : string.IsNullOrWhiteSpace(message)
                ? error
                : $"{message}: {error}";
        detail = RedactSecrets(detail.Trim());
        if (detail.Length > 700)
        {
            detail = $"{detail[..697]}...";
        }

        if (nextAttemptAt is not null)
        {
            detail = $"{detail} Nächster Versuch laut Log: {nextAttemptAt:dd.MM.yyyy HH:mm:ss} UTC.".Trim();
        }

        return string.IsNullOrWhiteSpace(detail) ? fallback : detail;
    }

    private static string RedactSecrets(string value)
    {
        return SecretPattern().Replace(value, "$1[ausgeblendet]");
    }

    private static void CollectSubjects(JsonElement element, string? propertyName, ISet<string> subjects)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectSubjects(property.Value, property.Name, subjects);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectSubjects(item, propertyName, subjects);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (IsSubjectProperty(propertyName))
                {
                    AddSubjectCandidates(value, subjects);
                }
                else if (propertyName is "msg" or "error")
                {
                    foreach (Match match in DomainPattern().Matches(value))
                    {
                        AddSubject(match.Value, subjects);
                    }
                }

                break;
        }
    }

    private static bool IsSubjectProperty(string? propertyName)
    {
        return propertyName is "identifier" or "identifiers" or "subject" or "subjects" or
            "domain" or "domains" or "name" or "names" or "san" or "sans";
    }

    private static void AddSubjectCandidates(string value, ISet<string> subjects)
    {
        var candidate = value.Trim().TrimEnd('.');
        if (candidate.StartsWith("_acme-challenge.", StringComparison.OrdinalIgnoreCase))
        {
            AddSubject(candidate, subjects);
            return;
        }

        foreach (Match match in DomainPattern().Matches(value))
        {
            AddSubject(match.Value, subjects);
        }
    }

    private static void AddSubject(string value, ISet<string> subjects)
    {
        var candidate = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (candidate.StartsWith("_acme-challenge.", StringComparison.Ordinal))
        {
            candidate = $"*.{candidate[16..]}";
        }

        if (candidate.Contains('.', StringComparison.Ordinal) &&
            !candidate.Contains("letsencrypt.org", StringComparison.Ordinal) &&
            !candidate.Contains("zerossl.com", StringComparison.Ordinal))
        {
            subjects.Add(candidate);
        }
    }

    private static CaddyCertificateLogState BuildState(IReadOnlyCollection<CaddyCertificateLogEvent> source)
    {
        var events = source
            .OrderBy(item => item.Timestamp)
            .ThenBy(item => item.State, StringComparer.Ordinal)
            .ToArray();
        var latest = events[^1];
        var lastAttemptAt = events
            .Where(IsAttemptEvent)
            .Select(item => (DateTimeOffset?)item.Timestamp)
            .LastOrDefault();
        var lastSuccessAt = events
            .Where(item => item.State == "succeeded")
            .Select(item => (DateTimeOffset?)item.Timestamp)
            .LastOrDefault();
        var nextAttemptAt = events
            .Where(item => item.NextAttemptAt is not null)
            .Select(item => item.NextAttemptAt)
            .LastOrDefault();
        var explicitAttempt = events
            .Where(item => item.Attempt is not null)
            .Select(item => item.Attempt!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var observedAttempts = events.Count(item => item.State is "started" or "failed" or "retry-scheduled");
        var consecutiveFailures = 0;
        foreach (var item in events.Reverse())
        {
            if (item.State == "succeeded")
            {
                break;
            }

            if (item.State == "failed")
            {
                consecutiveFailures++;
            }
        }

        var lastError = events
            .LastOrDefault(item => item.State is "failed" or "retry-scheduled")
            ?.Detail ?? string.Empty;
        var active = (latest.State is "started" or "challenging" or "propagating") &&
                     latest.Timestamp >= DateTimeOffset.UtcNow.AddMinutes(-15);
        return new CaddyCertificateLogState(
            lastAttemptAt,
            lastSuccessAt,
            nextAttemptAt,
            Math.Max(explicitAttempt, observedAttempts),
            consecutiveFailures,
            lastError,
            latest.State,
            active,
            events.TakeLast(MaximumEventsPerCertificate).Reverse().ToArray());
    }

    private static bool IsAttemptEvent(CaddyCertificateLogEvent item)
    {
        return item.State is "started" or "challenging" or "propagating" or "failed" or "retry-scheduled" or "succeeded";
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("ts", out var timestamp))
        {
            return null;
        }

        if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetDouble(out var unixSeconds))
        {
            var wholeSeconds = Math.Truncate(unixSeconds);
            var value = DateTimeOffset.FromUnixTimeSeconds((long)wholeSeconds);
            return value.AddSeconds(unixSeconds - wholeSeconds);
        }

        return timestamp.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   timestamp.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? ReadNextAttempt(JsonElement root, DateTimeOffset timestamp)
    {
        foreach (var propertyName in new[] { "retrying_in", "retry_in", "retry_after", "retry_delay" })
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                if (propertyName == "retry_after" && number > 1_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)number);
                }

                return timestamp.AddSeconds(Math.Max(number, 0));
            }

            if (value.ValueKind == JsonValueKind.String && TryParseDuration(value.GetString(), out var duration))
            {
                return timestamp.Add(duration);
            }
        }

        return null;
    }

    private static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        var match = DurationPattern().Match(value.Trim());
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        duration = match.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero,
        };
        return duration > TimeSpan.Zero;
    }

    private static int? ReadInteger(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(?:\*\.)?(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    [GeneratedRegex("""(?i)((?:password|token|api[_-]?key|secret)\s*[=:]\s*)[^\s,;\"']+""")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)(ms|s|m|h|d)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
