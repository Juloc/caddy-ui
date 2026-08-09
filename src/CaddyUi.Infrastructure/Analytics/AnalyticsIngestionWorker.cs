using System.Diagnostics;
using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Analytics;

public sealed class AnalyticsIngestionWorker : BackgroundService
{
    private readonly AnalyticsIngestionOptions _options;
    private readonly AnalyticsLogTailer _tailer;
    private readonly CaddyAccessLogParser _parser;
    private readonly RequestClassifier _classifier;
    private readonly AnalyticsIngestionStore _store;
    private readonly AnalyticsIngestionRuntimeMetrics _runtimeMetrics;
    private readonly AnalyticsClientKeyProvider _keyProvider;
    private readonly ILogger<AnalyticsIngestionWorker> _logger;

    public AnalyticsIngestionWorker(
        AnalyticsIngestionOptions options,
        AnalyticsLogTailer tailer,
        CaddyAccessLogParser parser,
        RequestClassifier classifier,
        AnalyticsIngestionStore store,
        AnalyticsIngestionRuntimeMetrics runtimeMetrics,
        AnalyticsClientKeyProvider keyProvider,
        ILogger<AnalyticsIngestionWorker> logger)
    {
        _options = options;
        _tailer = tailer;
        _parser = parser;
        _classifier = classifier;
        _store = store;
        _runtimeMetrics = runtimeMetrics;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Caddy analytics ingestion is disabled.");
            return;
        }

        if (_options.LogPaths.Count == 0)
        {
            _logger.LogWarning(
                "Caddy analytics ingestion is enabled, but no log paths are configured.");
            return;
        }

        var clientHashKey = await _keyProvider.GetKeyAsync(stoppingToken);
        _logger.LogInformation(
            "Caddy analytics ingestion started for {LogPathCount} log file(s) with batch size {BatchSize} and backlog delay {BacklogDelayMilliseconds} ms.",
            _options.LogPaths.Count,
            _options.BatchSize,
            _options.BacklogDelayMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var madeProgress = false;
            foreach (var path in _options.LogPaths)
            {
                try
                {
                    madeProgress |= await ProcessPathAsync(
                        path,
                        clientHashKey,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Caddy analytics ingestion failed for {LogPath}.",
                        path);
                }
            }

            var delayMilliseconds = madeProgress
                ? _options.BacklogDelayMilliseconds
                : _options.PollIntervalMilliseconds;
            await Task.Delay(
                TimeSpan.FromMilliseconds(delayMilliseconds),
                stoppingToken);
        }
    }

    private async Task<bool> ProcessPathAsync(
        string path,
        byte[] clientHashKey,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _store.GetCheckpointAsync(path, cancellationToken);
        var batch = await _tailer.ReadBatchAsync(
            path,
            checkpoint,
            _options.BatchSize,
            cancellationToken);
        if (batch is null || batch.Lines.Count == 0)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var requests = new List<ClassifiedRequest>(batch.Lines.Count);
        var failures = new List<AnalyticsIngestionFailure>();
        foreach (var line in batch.Lines)
        {
            if (_parser.TryParse(
                    line.Content,
                    path,
                    line.Offset,
                    out var requestEvent,
                    out var error) &&
                requestEvent is not null)
            {
                requests.Add(_classifier.Classify(requestEvent));
            }
            else
            {
                failures.Add(
                    new AnalyticsIngestionFailure(
                        line.Offset,
                        CaddyAccessLogParser.DescribeUnparsedLine(line.Content),
                        error));
            }
        }

        var result = await _store.PersistBatchAsync(
            path,
            batch.SourceIdentity,
            batch.EndOffset,
            requests,
            failures,
            clientHashKey,
            _options,
            cancellationToken);
        stopwatch.Stop();

        _runtimeMetrics.Record(
            new AnalyticsIngestionRuntimeSnapshot(
                path,
                batch.EndOffset,
                GetSourceLength(path, batch.EndOffset),
                DateTimeOffset.UtcNow,
                result.RequestsInserted,
                result.PageViewsInserted,
                result.FailuresInserted,
                stopwatch.ElapsedMilliseconds));

        _logger.LogDebug(
            "Ingested {RequestCount} requests, {PageViewCount} pageviews and {FailureCount} failures from {LogPath} in {ElapsedMilliseconds} ms.",
            result.RequestsInserted,
            result.PageViewsInserted,
            result.FailuresInserted,
            path,
            stopwatch.ElapsedMilliseconds);
        return true;
    }

    private static long GetSourceLength(string path, long minimumLength)
    {
        try
        {
            return Math.Max(minimumLength, new FileInfo(path).Length);
        }
        catch (IOException)
        {
            return minimumLength;
        }
        catch (UnauthorizedAccessException)
        {
            return minimumLength;
        }
    }
}
