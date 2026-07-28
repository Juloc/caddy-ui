using System.Net;
using System.Net.Sockets;
using CaddyUi.Domain.Security;

namespace CaddyUi.Application.Security;

public sealed class IpAddressClassifier
{
    private static readonly NetworkRange[] NonPublicRanges =
    [
        NetworkRange.Parse("0.0.0.0/8", IpAddressScope.Unspecified),
        NetworkRange.Parse("10.0.0.0/8", IpAddressScope.Private),
        NetworkRange.Parse("100.64.0.0/10", IpAddressScope.Shared),
        NetworkRange.Parse("127.0.0.0/8", IpAddressScope.Loopback),
        NetworkRange.Parse("169.254.0.0/16", IpAddressScope.LinkLocal),
        NetworkRange.Parse("172.16.0.0/12", IpAddressScope.Private),
        NetworkRange.Parse("192.0.0.0/24", IpAddressScope.Reserved),
        NetworkRange.Parse("192.0.2.0/24", IpAddressScope.Documentation),
        NetworkRange.Parse("192.168.0.0/16", IpAddressScope.Private),
        NetworkRange.Parse("192.88.99.0/24", IpAddressScope.Reserved),
        NetworkRange.Parse("198.18.0.0/15", IpAddressScope.Benchmark),
        NetworkRange.Parse("198.51.100.0/24", IpAddressScope.Documentation),
        NetworkRange.Parse("203.0.113.0/24", IpAddressScope.Documentation),
        NetworkRange.Parse("224.0.0.0/4", IpAddressScope.Multicast),
        NetworkRange.Parse("240.0.0.0/4", IpAddressScope.Reserved),
        NetworkRange.Parse("::/128", IpAddressScope.Unspecified),
        NetworkRange.Parse("::1/128", IpAddressScope.Loopback),
        NetworkRange.Parse("100::/64", IpAddressScope.Reserved),
        NetworkRange.Parse("2001:db8::/32", IpAddressScope.Documentation),
        NetworkRange.Parse("fc00::/7", IpAddressScope.Private),
        NetworkRange.Parse("fe80::/10", IpAddressScope.LinkLocal),
        NetworkRange.Parse("ff00::/8", IpAddressScope.Multicast),
    ];

    public IpAddressClassification Classify(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IPAddress.TryParse(value.Trim(), out var address))
        {
            throw new FormatException("The value is not a valid IPv4 or IPv6 address.");
        }

        return Classify(address);
    }

    public IpAddressClassification Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);

        foreach (var range in NonPublicRanges)
        {
            if (range.Contains(normalized))
            {
                return new IpAddressClassification(
                    normalized,
                    normalized.ToString(),
                    range.Scope,
                    false);
            }
        }

        var scope = normalized.AddressFamily switch
        {
            AddressFamily.InterNetwork => IpAddressScope.Public,
            AddressFamily.InterNetworkV6 when HasPrefix(normalized.GetAddressBytes(), [0x20], 3) =>
                IpAddressScope.Public,
            _ => IpAddressScope.Reserved,
        };
        return new IpAddressClassification(
            normalized,
            normalized.ToString(),
            scope,
            scope == IpAddressScope.Public);
    }

    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : new IPAddress(address.GetAddressBytes());
    }

    private static bool HasPrefix(
        ReadOnlySpan<byte> address,
        ReadOnlySpan<byte> network,
        int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (address.Length != network.Length && network.Length != wholeBytes + 1)
        {
            return false;
        }

        for (var index = 0; index < wholeBytes; index++)
        {
            if (address[index] != network[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (network[wholeBytes] & mask);
    }

    private sealed record NetworkRange(
        byte[] Network,
        int PrefixLength,
        AddressFamily AddressFamily,
        IpAddressScope Scope)
    {
        public bool Contains(IPAddress address)
        {
            return address.AddressFamily == AddressFamily &&
                HasPrefix(address.GetAddressBytes(), Network, PrefixLength);
        }

        public static NetworkRange Parse(string value, IpAddressScope scope)
        {
            var separator = value.LastIndexOf('/');
            if (separator <= 0 ||
                !IPAddress.TryParse(value[..separator], out var address) ||
                !int.TryParse(
                    value[(separator + 1)..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var prefixLength))
            {
                throw new InvalidOperationException($"Invalid built-in IP range: {value}");
            }

            var normalized = Normalize(address);
            var maximumPrefix = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > maximumPrefix)
            {
                throw new InvalidOperationException($"Invalid built-in IP prefix: {value}");
            }

            return new NetworkRange(
                ApplyMask(normalized.GetAddressBytes(), prefixLength),
                prefixLength,
                normalized.AddressFamily,
                scope);
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
}
