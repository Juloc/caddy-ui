using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyUi.Domain.Analytics;

namespace CaddyUi.Application.Analytics;

public sealed class CaddyAccessLogParser
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    public bool TryParse(
        string line,
        string sourceFile,
        long sourceOffset,
        out NormalizedRequestEvent? requestEvent,
        out string error)
    {
        requestEvent = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "The log line is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("request", out var request) ||
                request.ValueKind != JsonValueKind.Object)
            {
                error = "The JSON object does not contain a Caddy request.";
                return false;
            }

            var occurredAt = ReadTimestamp(root);
            var method = (ReadString(request, "method") ?? string.Empty).Trim().ToUpperInvariant();
            var host = NormalizeHost(ReadString(request, "host") ?? string.Empty);
            var rawUri = ReadString(request, "uri") ?? "/";
            var (path, queryString) = SplitUri(rawUri);
            var headers = request.TryGetProperty("headers", out var requestHeaders)
                ? requestHeaders
                : default;
            var responseHeaders = root.TryGetProperty("resp_headers", out var responseHeaderValue)
                ? responseHeaderValue
                : default;
            var remoteAddress = NormalizeAddress(
                ReadString(request, "client_ip") ??
                ReadString(request, "remote_ip") ??
                string.Empty);
            var status = ReadInt32(root, "status");
            var durationMilliseconds = Math.Max(0, ReadDouble(root, "duration") * 1000);
            var bytesSent = Math.Max(0, ReadInt64(root, "size"));
            var userAgent = ReadHeader(headers, "User-Agent");
            var referer = ReadHeader(headers, "Referer");
            var accept = ReadHeader(headers, "Accept");
            var secFetchDest = ReadHeader(headers, "Sec-Fetch-Dest");
            var upgrade = ReadHeader(headers, "Upgrade");
            var firstPartyClientIdentifier = ReadHeader(headers, "X-Caddy-Ui-Client");
            var contentType = ReadHeader(responseHeaders, "Content-Type");
            var sanitizedQuery = RedactQueryString(queryString);
            var sanitizedJson = RedactRawJson(line);

            if (occurredAt is null)
            {
                error = "The Caddy log timestamp is missing or invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(host))
            {
                error = "The Caddy request method or host is missing.";
                return false;
            }

            requestEvent = new NormalizedRequestEvent(
                Guid.NewGuid(),
                occurredAt.Value.ToUniversalTime(),
                sourceFile,
                sourceOffset,
                host,
                method,
                path,
                sanitizedQuery,
                status,
                durationMilliseconds,
                bytesSent,
                remoteAddress,
                userAgent,
                referer,
                accept,
                contentType,
                secFetchDest,
                upgrade,
                string.IsNullOrWhiteSpace(firstPartyClientIdentifier)
                    ? null
                    : firstPartyClientIdentifier.Trim(),
                sanitizedJson);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The log line is not valid JSON: {exception.Message}";
            return false;
        }
        catch (FormatException exception)
        {
            error = $"The log line contains an invalid value: {exception.Message}";
            return false;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            error = $"The log line contains an out-of-range value: {exception.Message}";
            return false;
        }
    }

    public static string DescribeUnparsedLine(string line)
    {
        var bytes = Utf8.GetBytes(line);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[unparsed caddy log; sha256={digest}; length={bytes.Length}]");
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("ts", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var unixSeconds))
        {
            var wholeSeconds = Math.Truncate(unixSeconds);
            var fractionalSeconds = unixSeconds - wholeSeconds;
            return DateTimeOffset.FromUnixTimeSeconds((long)wholeSeconds)
                .AddSeconds(fractionalSeconds);
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp;
        }

        return null;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int ReadInt32(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) &&
            value.TryGetInt32(out var result)
                ? result
                : 0;
    }

    private static long ReadInt64(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) &&
            value.TryGetInt64(out var result)
                ? result
                : 0;
    }

    private static double ReadDouble(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) &&
            value.TryGetDouble(out var result)
                ? result
                : 0;
    }

    private static string ReadHeader(JsonElement headers, string name)
    {
        if (headers.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in headers.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                return string.Join(
                    ", ",
                    property.Value
                        .EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? string.Empty)
                        .Where(item => item.Length > 0));
            }

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return string.Empty;
    }

    private static string NormalizeHost(string value)
    {
        var candidate = value.Trim().TrimEnd('.');
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (Uri.TryCreate($"http://{candidate}", UriKind.Absolute, out var uri))
        {
            return uri.IdnHost.ToLowerInvariant();
        }

        return candidate.ToLowerInvariant();
    }

    private static string? NormalizeAddress(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(candidate, out var address))
        {
            return address.ToString();
        }

        if (candidate.StartsWith("[", StringComparison.Ordinal) &&
            candidate.IndexOf(']') is var closingBracket &&
            closingBracket > 1 &&
            IPAddress.TryParse(candidate[1..closingBracket], out address))
        {
            return address.ToString();
        }

        var lastColon = candidate.LastIndexOf(':');
        if (lastColon > 0 &&
            candidate.Count(character => character == ':') == 1 &&
            IPAddress.TryParse(candidate[..lastColon], out address))
        {
            return address.ToString();
        }

        return null;
    }

    private static (string Path, string QueryString) SplitUri(string value)
    {
        var candidate = value.Trim();
        if (candidate.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            candidate = absolute.PathAndQuery;
        }

        if (candidate.Length == 0)
        {
            return ("/", string.Empty);
        }

        var separator = candidate.IndexOf('?');
        var path = separator < 0 ? candidate : candidate[..separator];
        var query = separator < 0 ? string.Empty : candidate[(separator + 1)..];

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = $"/{path}";
        }

        return (path, query);
    }

    private static string RedactRawJson(string line)
    {
        var node = JsonNode.Parse(line);
        if (node is not JsonObject root)
        {
            return "{}";
        }

        RedactHeaders(root["request"]?["headers"] as JsonObject);
        RedactHeaders(root["resp_headers"] as JsonObject);

        if (root["request"] is JsonObject request &&
            request["uri"] is JsonValue uriValue &&
            uriValue.TryGetValue<string>(out var uri))
        {
            var (path, query) = SplitUri(uri);
            request["uri"] = string.IsNullOrEmpty(query)
                ? path
                : $"{path}?{RedactQueryString(query)}";
        }

        return root.ToJsonString();
    }

    private static void RedactHeaders(JsonObject? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var key in headers.Select(item => item.Key).ToArray())
        {
            if (IsSensitiveName(key))
            {
                headers[key] = JsonValue.Create("[redacted]");
            }
        }
    }

    private static string RedactQueryString(string queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return string.Empty;
        }

        return string.Join(
            "&",
            queryString
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var separator = part.IndexOf('=');
                    var key = separator < 0 ? part : part[..separator];
                    if (!IsSensitiveName(Uri.UnescapeDataString(key.Replace('+', ' '))))
                    {
                        return part;
                    }

                    return $"{key}=[redacted]";
                }));
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim();

        return normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "code", StringComparison.OrdinalIgnoreCase);
    }
}
