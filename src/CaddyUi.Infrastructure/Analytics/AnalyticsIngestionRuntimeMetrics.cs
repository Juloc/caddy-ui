namespace CaddyUi.Infrastructure.Analytics;

public sealed record AnalyticsIngestionRuntimeSnapshot(
    string Source,
    long CheckpointOffset,
    long SourceLength,
    DateTimeOffset CompletedAt,
    int RequestsInserted,
    int PageViewsInserted,
    int FailuresInserted,
    long DurationMilliseconds)
{
    public long BacklogBytes => Math.Max(0, SourceLength - CheckpointOffset);
}

public sealed class AnalyticsIngestionRuntimeMetrics
{
    private AnalyticsIngestionRuntimeSnapshot? _latest;

    public AnalyticsIngestionRuntimeSnapshot? GetLatest()
    {
        return Volatile.Read(ref _latest);
    }

    public void Record(AnalyticsIngestionRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
    }
}
