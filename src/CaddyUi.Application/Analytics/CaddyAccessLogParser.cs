using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            var path = ReadPath(rawUri);
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
            var accept = ReadHeader(headers, "Accept");
            var secFetchDest = ReadHeader(headers, "Sec-Fetch-Dest");
            var upgrade = ReadHeader(headers, "Upgrade");
            var firstPartyClientIdentifier = ReadHeader(headers, "X-Caddy-Ui-Client");
            var contentType = ReadHeader(responseHeaders, "Content-Type");

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
                occurredAt.Value.ToUniversalTime(),
                sourceFile,
                sourceOffset,
                host[..Math.Min(host.Length, 253)],
                method[..Math.Min(method.Length, 32)],
                path[..Math.Min(path.Length, 2048)],
                status,
                durationMilliseconds,
                bytesSent,
                remoteAddress,
                userAgent[..Math.Min(userAgent.Length, 512)],
                accept[..Math.Min(accept.Length, 512)],
                contentType[..Math.Min(contentType.Length, 256)],
                secFetchDest[..Math.Min(secFetchDest.Length, 64)],
                upgrade[..Math.Min(upgrade.Length, 64)],
                string.IsNullOrWhiteSpace(firstPartyClientIdentifier)
                    ? null
                    : firstPartyClientIdentifier.Trim()[..Math.Min(firstPartyClientIdentifier.Trim().Length, 512)]);
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

    private static string ReadPath(string value)
    {
        var candidate = value.Trim();
        if (candidate.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            candidate = absolute.PathAndQuery;
        }

        if (candidate.Length == 0)
        {
            return "/";
        }

        var separator = candidate.IndexOf('?');
        var path = separator < 0 ? candidate : candidate[..separator];

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = $"/{path}";
        }

        return path;
    }

}
