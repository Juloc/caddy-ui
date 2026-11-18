using CaddyUi.Application.Analytics;
using CaddyUi.Infrastructure.Analytics;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AnalyticsLiveStreamTests
{
    [Fact]
    public void LiveDetails_AreBoundedAndClearedWhenViewersLeave()
    {
        var clock = new TestClock();
        var stream = new AnalyticsLiveStream(clock);
        var parser = new CaddyAccessLogParser();
        Assert.True(parser.TryParse("""{"ts":1785261600,"request":{"client_ip":"203.0.113.5","method":"GET","host":"example.test","uri":"/"},"status":200,"size":128}""",
            "/logs/access.log", 0, out var request, out _));
        var items = Enumerable.Repeat(new RequestClassifier().Classify(request!), 1100).ToArray();
        var filter = AnalyticsReadFilter.Create(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow(), limit: 1000);
        stream.Publish(items);
        var first = stream.Subscribe();
        Assert.Empty(stream.Read(first, 0, filter));
        stream.Publish(items);
        var rows = stream.Read(first, 0, filter);
        Assert.Equal(1000, rows.Count);
        Assert.Equal(1000, stream.Read(first, long.MaxValue, filter).Count);
        Assert.Empty(stream.Read(first, rows[^1].Sequence, filter));
        Assert.Empty(stream.Read(first, 0, filter with { Host = "other.test" }));
        var second = stream.Subscribe();
        stream.Unsubscribe(first);
        Assert.Equal(1000, stream.Read(second, 0, filter).Count);
        stream.Unsubscribe(second);
        Assert.Empty(stream.Read(stream.Subscribe(), 0, filter));
        stream.Publish(items);
        clock.Advance(TimeSpan.FromSeconds(21));
        stream.Publish(items);
        Assert.Empty(stream.Read(stream.Subscribe(), 0, filter));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
