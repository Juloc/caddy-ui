using System.Text;
using CaddyUi.Infrastructure.Analytics;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AnalyticsLogTailerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"caddy-ui-tailer-{Guid.NewGuid():N}");
    private readonly AnalyticsLogTailer _tailer = new();

    public AnalyticsLogTailerTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ReadBatch_ResumesAtCompleteUtf8Lines(string newline)
    {
        var path = Path.Combine(_directory, "access.log");
        var content = $"äöü{newline}{newline}third{newline}unfinished";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));

        var first = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, null, 2));
        Assert.Equal(new[] { "äöü", "" }, first.Lines.Select(line => line.Content));
        Assert.Equal(Encoding.UTF8.GetByteCount($"äöü{newline}{newline}"), first.EndOffset);
        Assert.Equal(0, first.Lines[0].Offset);
        Assert.Equal(first.Lines[0].EndOffset, first.Lines[1].Offset);

        var second = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, Checkpoint(path, first), 10));
        Assert.Equal("third", Assert.Single(second.Lines).Content);
        Assert.Equal(first.EndOffset, second.StartOffset);
        var idle = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, Checkpoint(path, second), 10));
        Assert.Empty(idle.Lines);
        Assert.Equal(second.EndOffset, idle.EndOffset);

        await File.AppendAllTextAsync(path, newline);
        var completed = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, Checkpoint(path, second), 10));
        Assert.Equal("unfinished", Assert.Single(completed.Lines).Content);
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(4 * 1024 * 1024 + 1)]
    public async Task ReadBatch_HandlesBufferBoundaries(int length)
    {
        var path = Path.Combine(_directory, "access.log");
        var line = new string('a', length - 2) + "ä";
        await File.WriteAllTextAsync(path, line + "\nnext\n", new UTF8Encoding(false));
        if (Encoding.UTF8.GetByteCount(line) > 4 * 1024 * 1024)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => _tailer.ReadBatchAsync(path, null, 10));
            return;
        }

        var batch = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, null, 10));
        Assert.Equal(new[] { line, "next" }, batch.Lines.Select(item => item.Content));
        Assert.Equal(new FileInfo(path).Length, batch.EndOffset);
    }

    [Fact]
    public async Task ReadBatch_RotationAndTruncationRestartAtZero()
    {
        var path = Path.Combine(_directory, "access.log");
        await File.WriteAllTextAsync(path, "first\nsecond\n");
        var first = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, null, 10));
        File.Move(path, path + ".1");
        await File.WriteAllTextAsync(path, "replacement\n");
        var rotated = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, Checkpoint(path, first), 10));
        Assert.Equal(0, rotated.StartOffset);
        Assert.Equal("replacement", Assert.Single(rotated.Lines).Content);

        await File.WriteAllTextAsync(path, "short\n");
        var truncated = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, Checkpoint(path, rotated), 10));
        Assert.Equal(0, truncated.StartOffset);
        Assert.Equal("short", Assert.Single(truncated.Lines).Content);
    }

    [Fact]
    public async Task ReadBatch_ReplacementWithSamePrefixRestartsAtZero()
    {
        var path = Path.Combine(_directory, "access.log");
        await File.WriteAllTextAsync(path, "same\nold\n");
        var first = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, null, 10));
        File.Move(path, path + ".1");
        await File.WriteAllTextAsync(path, "same\nreplacement-is-longer\n");
        var replaced = Assert.IsType<AnalyticsReadBatch>(await _tailer.ReadBatchAsync(path, Checkpoint(path, first), 10));
        Assert.NotEqual(first.SourceIdentity, replaced.SourceIdentity);
        Assert.Equal(0, replaced.StartOffset);
        Assert.Equal(new[] { "same", "replacement-is-longer" }, replaced.Lines.Select(line => line.Content));
    }

    [Fact]
    public async Task ReadBatch_MissingFileReturnsNull()
    {
        Assert.Null(await _tailer.ReadBatchAsync(Path.Combine(_directory, "missing.log"), null, 10));
    }

    private static AnalyticsCheckpoint Checkpoint(string path, AnalyticsReadBatch batch)
    {
        return new AnalyticsCheckpoint(path, batch.SourceIdentity, batch.EndOffset, null);
    }
}
