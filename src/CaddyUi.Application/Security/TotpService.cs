using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CaddyUi.Application.Security;

public sealed class TotpService
{
    private const int TimeStepSeconds = 30;
    private const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret()
    {
        return EncodeBase32(RandomNumberGenerator.GetBytes(20));
    }

    public string ComputeCode(string secret, DateTimeOffset timestamp)
    {
        var key = DecodeBase32(secret);
        var counter = timestamp.ToUnixTimeSeconds() / TimeStepSeconds;
        Span<byte> counterBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        var digest = HMACSHA1.HashData(key, counterBytes);
        var offset = digest[^1] & 0x0F;
        var binary =
            ((digest[offset] & 0x7F) << 24) |
            (digest[offset + 1] << 16) |
            (digest[offset + 2] << 8) |
            digest[offset + 3];
        var value = binary % (int)Math.Pow(10, Digits);
        return value.ToString($"D{Digits}", CultureInfo.InvariantCulture);
    }

    public bool VerifyCode(string secret, string? code, DateTimeOffset? timestamp = null)
    {
        var candidate = (code ?? string.Empty).Trim();
        if (candidate.Length != Digits || candidate.Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        var now = timestamp ?? DateTimeOffset.UtcNow;
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = ComputeCode(secret, now.AddSeconds(offset * TimeStepSeconds));
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate),
                    Encoding.ASCII.GetBytes(expected)))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string> GenerateRecoveryCodes(int count = 10)
    {
        if (count is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return Enumerable.Range(0, count)
            .Select(_ =>
            {
                var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                return $"{value[..4]}-{value[4..8]}-{value[8..12]}-{value[12..]}";
            })
            .ToArray();
    }

    public string HashRecoveryCode(string code)
    {
        var normalized = NormalizeRecoveryCode(code);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public string BuildProvisioningUri(string secret, string account, string issuer = "Caddy UI")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{label}?secret={Uri.EscapeDataString(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={TimeStepSeconds}");
    }

    private static string NormalizeRecoveryCode(string value)
    {
        return value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    private static string EncodeBase32(ReadOnlySpan<byte> value)
    {
        var output = new StringBuilder((value.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var item in value)
        {
            buffer = (buffer << 8) | item;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                output.Append(Base32Alphabet[(buffer >> bitsLeft) & 31]);
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return output.ToString();
    }

    private static byte[] DecodeBase32(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var result = new List<byte>((normalized.Length * 5) / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var character in normalized)
        {
            var index = Base32Alphabet.IndexOf(character);
            if (index < 0)
            {
                throw new FormatException("The TOTP secret is not valid Base32.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        return result.ToArray();
    }
}
