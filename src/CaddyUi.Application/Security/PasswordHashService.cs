using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CaddyUi.Application.Security;

public sealed record PasswordVerificationResult(bool Succeeded, string? UpgradedHash = null);

public sealed class PasswordHashService
{
    private const int Iterations = 210_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int MaximumPasswordLength = 1024;

    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length is < 12 or > MaximumPasswordLength)
        {
            throw new ArgumentException(
                $"Passwords must contain between 12 and {MaximumPasswordLength} characters.",
                nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashLength);

        return $"pbkdf2-sha256${Iterations}${Encode(salt)}${Encode(hash)}";
    }

    public PasswordVerificationResult Verify(string password, string encodedHash)
    {
        if (password.Length > MaximumPasswordLength || string.IsNullOrWhiteSpace(encodedHash))
        {
            return new PasswordVerificationResult(false);
        }

        if (encodedHash.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal))
        {
            return VerifyPbkdf2(password, encodedHash);
        }

        if (encodedHash.StartsWith("scrypt$", StringComparison.Ordinal))
        {
            var succeeded = VerifyLegacyScrypt(password, encodedHash);
            return new PasswordVerificationResult(
                succeeded,
                succeeded ? HashPassword(password) : null);
        }

        return new PasswordVerificationResult(false);
    }

    private static PasswordVerificationResult VerifyPbkdf2(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations is < 100_000 or > 2_000_000 ||
            !TryDecode(parts[2], out var salt) ||
            !TryDecode(parts[3], out var expected) ||
            salt.Length is < 16 or > 64 ||
            expected.Length is < 16 or > 64)
        {
            return new PasswordVerificationResult(false);
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        var succeeded = CryptographicOperations.FixedTimeEquals(actual, expected);

        return new PasswordVerificationResult(
            succeeded,
            succeeded && iterations < Iterations
                ? new PasswordHashService().HashPassword(password)
                : null);
    }

    private static bool VerifyLegacyScrypt(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 6 ||
            !int.TryParse(parts[1], out var cost) ||
            !int.TryParse(parts[2], out var blockSize) ||
            !int.TryParse(parts[3], out var parallelization) ||
            cost is < 4096 or > 262_144 ||
            (cost & (cost - 1)) != 0 ||
            blockSize is < 1 or > 16 ||
            parallelization is < 1 or > 4 ||
            !TryDecode(parts[4], out var salt) ||
            !TryDecode(parts[5], out var expected) ||
            salt.Length is < 8 or > 64 ||
            expected.Length is < 16 or > 64)
        {
            return false;
        }

        var actual = Scrypt.DeriveKey(
            Encoding.UTF8.GetBytes(password),
            salt,
            cost,
            blockSize,
            parallelization,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Encode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecode(string value, out byte[] result)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - (normalized.Length % 4)) % 4),
                '=');
            result = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            result = Array.Empty<byte>();
            return false;
        }
    }

    private static class Scrypt
    {
        private const int ChunkLength = 64;

        public static byte[] DeriveKey(
            byte[] password,
            byte[] salt,
            int cost,
            int blockSize,
            int parallelization,
            int outputLength)
        {
            var blockLength = checked(128 * blockSize);
            var state = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                1,
                HashAlgorithmName.SHA256,
                checked(parallelization * blockLength));

            for (var index = 0; index < parallelization; index++)
            {
                Romix(state.AsSpan(index * blockLength, blockLength), cost, blockSize);
            }

            var result = Rfc2898DeriveBytes.Pbkdf2(
                password,
                state,
                1,
                HashAlgorithmName.SHA256,
                outputLength);
            CryptographicOperations.ZeroMemory(state);
            return result;
        }

        private static void Romix(Span<byte> block, int cost, int blockSize)
        {
            var length = block.Length;
            var x = block.ToArray();
            var memory = GC.AllocateUninitializedArray<byte>(checked(cost * length));

            for (var index = 0; index < cost; index++)
            {
                x.CopyTo(memory.AsSpan(index * length, length));
                var next = BlockMix(x, blockSize);
                CryptographicOperations.ZeroMemory(x);
                x = next;
            }

            for (var index = 0; index < cost; index++)
            {
                var memoryIndex = (int)(Integerify(x, blockSize) & (ulong)(cost - 1));
                Xor(x, memory.AsSpan(memoryIndex * length, length));
                var next = BlockMix(x, blockSize);
                CryptographicOperations.ZeroMemory(x);
                x = next;
            }

            x.CopyTo(block);
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(memory);
        }

        private static byte[] BlockMix(byte[] input, int blockSize)
        {
            var output = GC.AllocateUninitializedArray<byte>(input.Length);
            Span<byte> x = stackalloc byte[ChunkLength];
            input.AsSpan(input.Length - ChunkLength, ChunkLength).CopyTo(x);

            for (var index = 0; index < blockSize * 2; index++)
            {
                Xor(x, input.AsSpan(index * ChunkLength, ChunkLength));
                Salsa208(x);

                var targetBlock = index % 2 == 0
                    ? index / 2
                    : blockSize + (index / 2);
                x.CopyTo(output.AsSpan(targetBlock * ChunkLength, ChunkLength));
            }

            CryptographicOperations.ZeroMemory(x);
            return output;
        }

        private static ulong Integerify(byte[] value, int blockSize)
        {
            var offset = (2 * blockSize - 1) * ChunkLength;
            return BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(offset, sizeof(ulong)));
        }

        private static void Xor(Span<byte> target, ReadOnlySpan<byte> value)
        {
            for (var index = 0; index < target.Length; index++)
            {
                target[index] ^= value[index];
            }
        }

        private static void Salsa208(Span<byte> block)
        {
            Span<uint> state = stackalloc uint[16];
            Span<uint> working = stackalloc uint[16];
            for (var index = 0; index < 16; index++)
            {
                state[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    block.Slice(index * sizeof(uint), sizeof(uint)));
                working[index] = state[index];
            }

            for (var round = 0; round < 8; round += 2)
            {
                working[4] ^= RotateLeft(working[0] + working[12], 7);
                working[8] ^= RotateLeft(working[4] + working[0], 9);
                working[12] ^= RotateLeft(working[8] + working[4], 13);
                working[0] ^= RotateLeft(working[12] + working[8], 18);

                working[9] ^= RotateLeft(working[5] + working[1], 7);
                working[13] ^= RotateLeft(working[9] + working[5], 9);
                working[1] ^= RotateLeft(working[13] + working[9], 13);
                working[5] ^= RotateLeft(working[1] + working[13], 18);

                working[14] ^= RotateLeft(working[10] + working[6], 7);
                working[2] ^= RotateLeft(working[14] + working[10], 9);
                working[6] ^= RotateLeft(working[2] + working[14], 13);
                working[10] ^= RotateLeft(working[6] + working[2], 18);

                working[3] ^= RotateLeft(working[15] + working[11], 7);
                working[7] ^= RotateLeft(working[3] + working[15], 9);
                working[11] ^= RotateLeft(working[7] + working[3], 13);
                working[15] ^= RotateLeft(working[11] + working[7], 18);

                working[1] ^= RotateLeft(working[0] + working[3], 7);
                working[2] ^= RotateLeft(working[1] + working[0], 9);
                working[3] ^= RotateLeft(working[2] + working[1], 13);
                working[0] ^= RotateLeft(working[3] + working[2], 18);

                working[6] ^= RotateLeft(working[5] + working[4], 7);
                working[7] ^= RotateLeft(working[6] + working[5], 9);
                working[4] ^= RotateLeft(working[7] + working[6], 13);
                working[5] ^= RotateLeft(working[4] + working[7], 18);

                working[11] ^= RotateLeft(working[10] + working[9], 7);
                working[8] ^= RotateLeft(working[11] + working[10], 9);
                working[9] ^= RotateLeft(working[8] + working[11], 13);
                working[10] ^= RotateLeft(working[9] + working[8], 18);

                working[12] ^= RotateLeft(working[15] + working[14], 7);
                working[13] ^= RotateLeft(working[12] + working[15], 9);
                working[14] ^= RotateLeft(working[13] + working[12], 13);
                working[15] ^= RotateLeft(working[14] + working[13], 18);
            }

            for (var index = 0; index < 16; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    block.Slice(index * sizeof(uint), sizeof(uint)),
                    working[index] + state[index]);
            }

            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(state));
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(working));
        }

        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }
    }
}
