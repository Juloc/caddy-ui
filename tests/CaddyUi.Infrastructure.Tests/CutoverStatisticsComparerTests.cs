using CaddyUi.Infrastructure.Cutover;

namespace CaddyUi.Infrastructure.Tests;

public sealed class CutoverStatisticsComparerTests
{
    [Fact]
    public void EqualStatistics_AreWithinTolerance()
    {
        var from = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
        var legacy = new LegacyStatisticsSnapshot(
            from.AddDays(1),
            from,
            from.AddDays(1),
            1000,
            100,
            80,
            50,
            5);
        var dotNet = new CutoverStatistics(1000, 100, 80, 50, 5);

        var report = CutoverStatisticsComparer.Compare(
            legacy,
            dotNet,
            maximumDifferencePercent: 1,
            capturedAt: from.AddDays(1));

        Assert.True(report.IsWithinTolerance);
        Assert.All(report.Metrics, metric => Assert.Equal(0, metric.AbsoluteDifference));
    }

    [Fact]
    public void DifferenceAboveTolerance_BlocksComparison()
    {
        var from = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
        var legacy = new LegacyStatisticsSnapshot(
            from.AddDays(1),
            from,
            from.AddDays(1),
            1000,
            100,
            80,
            50,
            5);
        var dotNet = new CutoverStatistics(900, 100, 80, 50, 5);

        var report = CutoverStatisticsComparer.Compare(
            legacy,
            dotNet,
            maximumDifferencePercent: 5,
            capturedAt: from.AddDays(1));

        Assert.False(report.IsWithinTolerance);
        var requests = Assert.Single(report.Metrics, metric => metric.Metric == "requests");
        Assert.Equal(10, requests.DifferencePercent, precision: 6);
        Assert.False(requests.WithinTolerance);
    }

    [Fact]
    public void ZeroLegacyValue_UsesStableDenominator()
    {
        var from = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
        var legacy = new LegacyStatisticsSnapshot(
            from.AddHours(1),
            from,
            from.AddHours(1),
            0,
            0,
            0,
            0,
            0);
        var dotNet = new CutoverStatistics(0, 0, 0, 0, 1);

        var report = CutoverStatisticsComparer.Compare(
            legacy,
            dotNet,
            maximumDifferencePercent: 5,
            capturedAt: from.AddHours(1));

        var errors = Assert.Single(report.Metrics, metric => metric.Metric == "errors");
        Assert.Equal(100, errors.DifferencePercent, precision: 6);
        Assert.False(errors.WithinTolerance);
    }

    [Fact]
    public void SnapshotWithInvalidWindow_IsRejected()
    {
        const string json =
            """
            {
              "capturedAt": "2026-07-29T00:00:00Z",
              "windowStart": "2026-07-29T00:00:00Z",
              "windowEnd": "2026-07-29T00:00:00Z",
              "requests": 1,
              "pageViews": 1,
              "sessions": 1,
              "clients": 1,
              "errors": 0
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => LegacyStatisticsSnapshot.Parse(json));

        Assert.Contains("window end", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
