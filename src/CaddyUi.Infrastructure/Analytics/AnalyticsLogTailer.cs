using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CaddyUi.Infrastructure.Analytics;

public sealed record AnalyticsCheckpoint(
    string Source,
    string SourceIdentity,
    long ByteOffset,
    DateTimeOffset? LastEventAt);

public sealed record AnalyticsLogLine(
    long Offset,
    long EndOffset,
    string Content);

public sealed record AnalyticsReadBatch(
    string SourceIdentity,
    long StartOffset,
    long EndOffset,
    IReadOnlyList<AnalyticsLogLine> Lines);

public sealed class AnalyticsLogTailer
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

    public async Task<AnalyticsReadBatch?> ReadBatchAsync(
        string path,
        AnalyticsCheckpoint? checkpoint,
        int maximumLines,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumLines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLines));
        }

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var identity = await ComputeIdentityAsync(path, stream, cancellationToken);
        var startOffset =
            checkpoint is not null &&
            string.Equals(checkpoint.SourceIdentity, identity, StringComparison.Ordinal) &&
            checkpoint.ByteOffset >= 0 &&
            checkpoint.ByteOffset <= stream.Length
                ? checkpoint.ByteOffset
                : 0;

        stream.Position = startOffset;
        var lines = new List<AnalyticsLogLine>(Math.Min(maximumLines, 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var currentLine = new MemoryStream();
        var absoluteOffset = startOffset;
        var lineOffset = startOffset;
        var endOffset = startOffset;

        try
        {
            while (lines.Count < maximumLines)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var index = 0;
                while (index < read && lines.Count < maximumLines)
                {
                    var newline = buffer.AsSpan(index, read - index).IndexOf((byte)'\n');
                    var length = newline < 0 ? read - index : newline;
                    if (currentLine.Length + length > MaximumLineBytes)
                    {
                        throw new InvalidDataException(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"A Caddy log line exceeds {MaximumLineBytes} bytes."));
                    }

                    currentLine.Write(buffer, index, length);
                    index += length;
                    absoluteOffset += length;
                    if (newline < 0)
                    {
                        continue;
                    }

                    index++;
                    absoluteOffset++;
                    var bytes = currentLine.GetBuffer();
                    var lineLength = (int)currentLine.Length;
                    if (lineLength > 0 && bytes[lineLength - 1] == (byte)'\r')
                    {
                        lineLength--;
                    }

                    lines.Add(new AnalyticsLogLine(lineOffset, absoluteOffset, Utf8.GetString(bytes, 0, lineLength)));
                    currentLine.SetLength(0);
                    lineOffset = absoluteOffset;
                    endOffset = absoluteOffset;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new AnalyticsReadBatch(
            identity,
            startOffset,
            endOffset,
            lines);
    }

    private static async Task<string> ComputeIdentityAsync(
        string path,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var originalPosition = stream.Position;
        stream.Position = 0;

        var prefix = new byte[(int)Math.Min(stream.Length, 4096L)];
        var read = prefix.Length == 0
            ? 0
            : await stream.ReadAsync(prefix, cancellationToken);
        stream.Position = originalPosition;

        var firstNewline = Array.IndexOf(prefix, (byte)'\n', 0, read);
        var stablePrefixLength = firstNewline < 0 ? 0 : firstNewline + 1;
        var file = new FileInfo(path);
        var descriptor = Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Path.GetFullPath(path)}\n{file.CreationTimeUtc.Ticks}\n"));
        var combined = new byte[descriptor.Length + stablePrefixLength];
        Buffer.BlockCopy(descriptor, 0, combined, 0, descriptor.Length);
        Buffer.BlockCopy(
            prefix,
            0,
            combined,
            descriptor.Length,
            stablePrefixLength);
        return Convert.ToHexStringLower(SHA256.HashData(combined));
    }
}
