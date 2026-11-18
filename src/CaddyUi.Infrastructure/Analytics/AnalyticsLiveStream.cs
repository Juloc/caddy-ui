using CaddyUi.Domain.Analytics;

namespace CaddyUi.Infrastructure.Analytics;

public sealed record LiveRequest(long Sequence, RequestAnalyticsRow Request);

public sealed class AnalyticsLiveStream(TimeProvider timeProvider)
{
    private const int MaximumRows = 1000;
    private const int MaximumViewers = 64;
    private static readonly TimeSpan ViewerLease = TimeSpan.FromSeconds(20);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _viewers = new();
    private readonly Queue<LiveRequest> _rows = new();
    private long _sequence;

    public Guid Subscribe()
    {
        lock (_gate)
        {
            RemoveExpiredViewers();
            if (_viewers.Count >= MaximumViewers)
            {
                throw new InvalidOperationException("The live-view connection limit has been reached.");
            }
            var id = Guid.NewGuid();
            _viewers.Add(id, timeProvider.GetUtcNow());
            return id;
        }
    }

    public void Unsubscribe(Guid id)
    {
        lock (_gate)
        {
            _viewers.Remove(id);
            if (_viewers.Count == 0)
                _rows.Clear();
        }
    }

    public IReadOnlyList<LiveRequest> Read(Guid viewer, long after, AnalyticsReadFilter filter)
    {
        lock (_gate)
        {
            RemoveExpiredViewers();
            if (!_viewers.ContainsKey(viewer))
                return Array.Empty<LiveRequest>();
            _viewers[viewer] = timeProvider.GetUtcNow();
            if (after > _sequence)
                after = 0;
            return _rows.Where(item => item.Sequence > after &&
                (filter.Host.Length == 0 || item.Request.Host == filter.Host) &&
                (filter.ActorType.Length == 0 || item.Request.ActorType == filter.ActorType) &&
                (filter.RequestType.Length == 0 || item.Request.RequestType == filter.RequestType) &&
                (filter.StatusClass.Length == 0 || $"{item.Request.Status / 100}xx" == filter.StatusClass))
                .Take(Math.Clamp(filter.Limit, 1, MaximumRows)).ToArray();
        }
    }

    public void Publish(IReadOnlyList<ClassifiedRequest> requests)
    {
        lock (_gate)
        {
            RemoveExpiredViewers();
            if (_viewers.Count == 0)
                return;
            foreach (var item in requests)
            {
                var request = item.Request;
                _rows.Enqueue(new LiveRequest(++_sequence, new RequestAnalyticsRow(request.OccurredAt, request.Host,
                    request.Method, request.Path, request.Status, item.RequestType.ToStorageValue(), item.ActorType.ToStorageValue(),
                    request.DurationMilliseconds, request.BytesSent, request.RemoteAddress ?? string.Empty, string.Empty)));
                while (_rows.Count > MaximumRows)
                    _rows.Dequeue();
            }
        }
    }

    private void RemoveExpiredViewers()
    {
        var cutoff = timeProvider.GetUtcNow() - ViewerLease;
        foreach (var id in _viewers.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            _viewers.Remove(id);
        if (_viewers.Count == 0)
            _rows.Clear();
    }
}
