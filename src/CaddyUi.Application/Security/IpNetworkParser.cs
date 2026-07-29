using System.Net;
using System.Net.Sockets;

namespace CaddyUi.Application.Security;

public sealed record NormalizedIpNetwork(
    IPAddress NetworkAddress,
    int PrefixLength,
    string Cidr,
    bool IsSingleAddress);

public static class IpNetworkParser
{
    public static NormalizedIpNetwork Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim();
        var separator = candidate.LastIndexOf('/');
        var addressText = separator < 0 ? candidate : candidate[..separator];
        if (!IPAddress.TryParse(addressText, out var parsedAddress))
        {
            throw new FormatException("The block target is not a valid IPv4 or IPv6 address.");
        }

        var address = IpAddressClassifier.Normalize(parsedAddress);
        var maximumPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefixLength = separator < 0
            ? maximumPrefix
            : ParsePrefix(candidate[(separator + 1)..], maximumPrefix);
        var networkBytes = ApplyMask(address.GetAddressBytes(), prefixLength);
        var networkAddress = new IPAddress(networkBytes);
        return new NormalizedIpNetwork(
            networkAddress,
            prefixLength,
            $"{networkAddress}/{prefixLength}",
            prefixLength == maximumPrefix);
    }

    private static int ParsePrefix(string value, int maximum)
    {
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var prefixLength) ||
            prefixLength < 0 ||
            prefixLength > maximum)
        {
            throw new FormatException("The block target contains an invalid prefix length.");
        }

        return prefixLength;
    }

    private static byte[] ApplyMask(byte[] address, int prefixLength)
    {
        var result = address.ToArray();
        for (var bit = prefixLength; bit < result.Length * 8; bit++)
        {
            result[bit / 8] &= (byte)~(1 << (7 - (bit % 8)));
        }

        return result;
    }
}
