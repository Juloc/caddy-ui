using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyUi.Application.Security;
using CaddyUi.Domain.Security;

namespace CaddyUi.Infrastructure.Security;

public sealed class RipeStatIpIntelligenceProvider : IIpIntelligenceProvider
{
    private readonly HttpClient _httpClient;
    private readonly IpAddressClassifier _classifier;
    private readonly IpSecurityOptions _options;
    private readonly TimeProvider _timeProvider;

    public RipeStatIpIntelligenceProvider(
        HttpClient httpClient,
        IpAddressClassifier classifier,
        IpSecurityOptions options,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _classifier = classifier;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<IpIntelligenceResult> LookupAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var classification = _classifier.Classify(address);
        var now = _timeProvider.GetUtcNow();
        if (!classification.ExternalLookupAllowed)
        {
            return new IpIntelligenceResult(
                classification.Address,
                classification.Scope,
                true,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "local",
                now,
                now.AddHours(_options.SuccessCacheHours),
                string.Empty,
                "{}");
        }

        try
        {
            var resource = Uri.EscapeDataString(classification.NormalizedAddress);
            using var networkDocument = await GetDocumentAsync(
                $"data/network-info/data.json?resource={resource}&sourceapp=caddy-ui",
                cancellationToken);
            var networkData = GetData(networkDocument.RootElement, "network-info");
            var prefix = ReadString(networkData, "prefix");
            var asns = ReadAsns(networkData);
            var asn = asns.FirstOrDefault() ?? string.Empty;
            string holder = string.Empty;
            string registry = string.Empty;
            JsonNode? overviewPayload = null;

            if (asn.Length > 0)
            {
                using var overviewDocument = await GetDocumentAsync(
                    $"data/as-overview/data.json?resource={Uri.EscapeDataString(asn)}&sourceapp=caddy-ui",
                    cancellationToken);
                var overviewData = GetData(overviewDocument.RootElement, "as-overview");
                holder = ReadString(overviewData, "holder");
                if (overviewData.TryGetProperty("block", out var block) &&
                    block.ValueKind == JsonValueKind.Object)
                {
                    registry = ReadString(block, "name");
                }

                overviewPayload = JsonNode.Parse(overviewData.GetRawText());
            }

            var payload = new JsonObject
            {
                ["networkInfo"] = JsonNode.Parse(networkData.GetRawText()),
                ["asOverview"] = overviewPayload,
            };
            return new IpIntelligenceResult(
                classification.Address,
                classification.Scope,
                prefix.Length > 0 || asn.Length > 0,
                asn,
                prefix,
                holder,
                registry,
                "ripestat",
                now,
                now.AddHours(_options.SuccessCacheHours),
                string.Empty,
                payload.ToJsonString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(classification, now, "RIPEstat lookup timed out.");
        }
        catch (HttpRequestException exception)
        {
            return Failure(classification, now, SanitizeError(exception.Message));
        }
        catch (JsonException exception)
        {
            return Failure(classification, now, SanitizeError(exception.Message));
        }
        catch (InvalidDataException exception)
        {
            return Failure(classification, now, SanitizeError(exception.Message));
        }
    }

    private async Task<JsonDocument> GetDocumentAsync(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static JsonElement GetData(JsonElement root, string endpoint)
    {
        var status = ReadString(root, "status");
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var message = ReadString(root, "message");
            throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"RIPEstat {endpoint} returned status '{status}': {message}"));
        }

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"RIPEstat {endpoint} returned no data object.");
        }

        return data;
    }

    private static IReadOnlyList<string> ReadAsns(JsonElement data)
    {
        if (!data.TryGetProperty("asns", out var asns) ||
            asns.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return asns.EnumerateArray()
            .Select(value => value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString() ?? string.Empty,
                _ => string.Empty,
            })
            .Where(value => value.Length > 0)
            .Select(value => value.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                ? value.ToUpperInvariant()
                : $"AS{value}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private IpIntelligenceResult Failure(
        IpAddressClassification classification,
        DateTimeOffset now,
        string error)
    {
        return new IpIntelligenceResult(
            classification.Address,
            classification.Scope,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "ripestat",
            now,
            now.AddMinutes(_options.FailureCacheMinutes),
            error,
            "{}");
    }

    private static string ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static string SanitizeError(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }
}
