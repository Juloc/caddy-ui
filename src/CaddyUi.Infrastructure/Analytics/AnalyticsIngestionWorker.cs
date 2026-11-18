using System.Diagnostics;
using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Analytics;

public sealed class AnalyticsIngestionWorker(AnalyticsIngestionOptions options, AnalyticsLogTailer tailer, CaddyAccessLogParser parser,
    RequestClassifier classifier, AnalyticsIngestionStore store, AnalyticsIngestionRuntimeMetrics runtimeMetrics,
    AnalyticsClientKeyProvider keyProvider, AnalyticsLiveStream liveStream, ILogger<AnalyticsIngestionWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, AnalyticsCheckpoint?> _checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingBatch> _pending = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || options.LogPaths.Count == 0)
            return;
        var clientHashKey = await keyProvider.GetKeyAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var hasBacklog = false;
                foreach (var path in options.LogPaths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            if (!_checkpoints.TryGetValue(path, out var checkpoint))
                            {
                                checkpoint = await store.GetCheckpointAsync(path, stoppingToken);
                                _checkpoints[path] = checkpoint;
                            }
                            _pending.TryGetValue(path, out var pending);
                            var remaining = 5000 - (pending?.Count ?? 0);
                            var batch = await tailer.ReadBatchAsync(path, pending?.Checkpoint ?? checkpoint, Math.Min(options.BatchSize, remaining), stoppingToken);
                            if (batch is not null && batch.Lines.Count > 0)
                            {
                                if (pending is not null && pending.Checkpoint.SourceIdentity != batch.SourceIdentity)
                                {
                                    await FlushAsync(path, pending, clientHashKey, stoppingToken);
                                    pending = null;
                                }
                                pending ??= new PendingBatch(new AnalyticsCheckpoint(path, batch.SourceIdentity, batch.StartOffset, null));
                                foreach (var line in batch.Lines)
                                {
                                    if (parser.TryParse(line.Content, path, line.Offset, out var request, out var error) && request is not null)
                                        pending.Requests.Add(classifier.Classify(request));
                                    else
                                        pending.Failures.Add(new AnalyticsIngestionFailure(line.Offset, CaddyAccessLogParser.DescribeUnparsedLine(line.Content), error));
                                }
                                liveStream.Publish(pending.Requests.Skip(pending.PublishedCount).ToArray());
                                pending.PublishedCount = pending.Requests.Count;
                                pending.Checkpoint = new AnalyticsCheckpoint(path, batch.SourceIdentity, batch.EndOffset, null);
                                _pending[path] = pending;
                                hasBacklog |= batch.HasMoreData;
                            }
                        }
                        if (_pending.TryGetValue(path, out var queued) &&
                            (queued.Count >= 5000 || queued.Elapsed.Elapsed >= TimeSpan.FromSeconds(options.FlushIntervalSeconds)))
                            await FlushAsync(path, queued, clientHashKey, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _pending.Remove(path);
                        _checkpoints.Remove(path);
                        logger.LogError(exception, "Analytics ingestion failed for {LogPath}; the committed checkpoint will be reloaded.", path);
                    }
                }
                await Task.Delay(hasBacklog ? options.BacklogDelayMilliseconds : options.PollIntervalMilliseconds, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var pair in _pending.ToArray())
            {
                try
                {
                    await FlushAsync(pair.Key, pair.Value, clientHashKey, timeout.Token);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Uncommitted analytics will resume from the durable checkpoint on restart.");
                }
            }
        }
    }

    private async Task FlushAsync(string path, PendingBatch pending, byte[] key, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var checkpoint = pending.Checkpoint;
        var result = await store.PersistBatchAsync(path, checkpoint.SourceIdentity, checkpoint.ByteOffset, pending.Requests, pending.Failures, key, options, cancellationToken);
        _checkpoints[path] = checkpoint;
        _pending.Remove(path);
        var length = File.Exists(path) ? Math.Max(checkpoint.ByteOffset, new FileInfo(path).Length) : checkpoint.ByteOffset;
        runtimeMetrics.Record(new AnalyticsIngestionRuntimeSnapshot(path, checkpoint.ByteOffset, length, DateTimeOffset.UtcNow,
            result.RequestsProcessed, result.TrafficBucketsWritten, result.FailuresInserted, elapsed.ElapsedMilliseconds));
    }

    private sealed class PendingBatch(AnalyticsCheckpoint checkpoint)
    {
        public AnalyticsCheckpoint Checkpoint { get; set; } = checkpoint;
        public List<ClassifiedRequest> Requests { get; } = new();
        public List<AnalyticsIngestionFailure> Failures { get; } = new();
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public int PublishedCount { get; set; }
        public int Count => Requests.Count + Failures.Count;
    }
}
