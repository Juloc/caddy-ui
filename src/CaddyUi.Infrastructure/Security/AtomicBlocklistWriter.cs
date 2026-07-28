using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CaddyUi.Infrastructure.Security;

public sealed record BlocklistEntry(
    IPAddress Address,
    DateTimeOffset ExpiresAt,
    string Reason);

public sealed record BlocklistWriteReceipt(
    string Path,
    bool PreviousFileExisted,
    byte[] PreviousContent,
    string AppliedDigest);

public sealed class AtomicBlocklistWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<BlocklistWriteReceipt> ApplyAsync(
        string path,
        IReadOnlyList<BlocklistEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("The blocklist path has no parent directory.");
        Directory.CreateDirectory(directory);

        var existed = File.Exists(fullPath);
        var previous = existed
            ? await File.ReadAllBytesAsync(fullPath, cancellationToken)
            : Array.Empty<byte>();
        var rendered = Render(entries);
        var content = Utf8.GetBytes(rendered);
        await WriteAtomicAsync(fullPath, content, cancellationToken);
        await VerifyAsync(fullPath, entries, cancellationToken);
        return new BlocklistWriteReceipt(
            fullPath,
            existed,
            previous,
            Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    public async Task RollbackAsync(
        BlocklistWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.PreviousFileExisted)
        {
            await WriteAtomicAsync(receipt.Path, receipt.PreviousContent, cancellationToken);
        }
        else if (File.Exists(receipt.Path))
        {
            File.Delete(receipt.Path);
        }
    }

    public static string Render(IReadOnlyList<BlocklistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var lines = entries
            .Where(entry => entry.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderBy(entry => entry.Address.AddressFamily)
            .ThenBy(entry => entry.Address.ToString(), StringComparer.Ordinal)
            .Select(entry =>
                $"{entry.Address}|{entry.ExpiresAt:O}|{SanitizeReason(entry.Reason)}");
        return string.Join('\n', lines) + (entries.Count > 0 ? "\n" : string.Empty);
    }

    private static async Task WriteAtomicAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("The blocklist path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task VerifyAsync(
        string path,
        IReadOnlyList<BlocklistEntry> expected,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, Utf8, cancellationToken);
        var parsed = new Dictionary<string, (DateTimeOffset ExpiresAt, string Reason)>(StringComparer.Ordinal);
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var parts = line.Split('|', 3);
            if (parts.Length != 3 ||
                !IPAddress.TryParse(parts[0], out var address) ||
                !DateTimeOffset.TryParse(
                    parts[1],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var expiresAt))
            {
                throw new InvalidDataException("The rendered blocklist could not be verified.");
            }

            parsed[address.ToString()] = (expiresAt, parts[2]);
        }

        var expectedActive = expected
            .Where(entry => entry.ExpiresAt > DateTimeOffset.UtcNow)
            .ToDictionary(entry => entry.Address.ToString(), StringComparer.Ordinal);
        if (parsed.Count != expectedActive.Count ||
            expectedActive.Any(pair =>
                !parsed.TryGetValue(pair.Key, out var actual) ||
                actual.ExpiresAt != pair.Value.ExpiresAt ||
                !string.Equals(actual.Reason, SanitizeReason(pair.Value.Reason), StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The rendered blocklist does not match the active rules.");
        }
    }

    private static string SanitizeReason(string value)
    {
        var normalized = value
            .Replace('|', '/')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }
}
