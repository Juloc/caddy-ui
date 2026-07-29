using System.Security.Cryptography;
using System.Text;
using CaddyUi.Domain.Analytics;

namespace CaddyUi.Application.Analytics;

public sealed record AnalyticsClientIdentity(
    string ClientKey,
    string? FirstPartyIdentifierHash,
    bool Estimated);

public static class AnalyticsClientFingerprint
{
    public static AnalyticsClientIdentity? Create(
        NormalizedRequestEvent request,
        ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (key.IsEmpty)
        {
            throw new ArgumentException("The analytics client hash key must not be empty.", nameof(key));
        }

        if (!string.IsNullOrWhiteSpace(request.FirstPartyClientIdentifier))
        {
            var normalizedIdentifier = request.FirstPartyClientIdentifier.Trim();
            var identifierHash = Hash(key, $"first-party\n{normalizedIdentifier}");
            return new AnalyticsClientIdentity(
                $"fp:{identifierHash}",
                identifierHash,
                false);
        }

        var address = request.RemoteAddress?.Trim() ?? string.Empty;
        var userAgent = request.UserAgent.Trim();
        if (address.Length == 0 && userAgent.Length == 0)
        {
            return null;
        }

        return new AnalyticsClientIdentity(
            $"estimated:{Hash(key, $"fallback\n{address}\n{userAgent}")}",
            null,
            true);
    }

    private static string Hash(ReadOnlySpan<byte> key, string value)
    {
        return Convert.ToHexStringLower(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    }
}
