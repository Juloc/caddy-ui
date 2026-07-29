using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaddyUi.Infrastructure.Cutover;

[JsonConverter(typeof(JsonStringEnumConverter<CutoverCheckState>))]
public enum CutoverCheckState
{
    Passed,
    Warning,
    Blocked,
}

public sealed record CutoverCheck(
    string Code,
    string Title,
    CutoverCheckState State,
    string Detail,
    string Remediation = "");

public sealed record CutoverInventory(
    long Users,
    long Domains,
    long Routes,
    long Requests,
    long PageViews,
    long Sessions,
    long Clients,
    DateTimeOffset? FirstRequestAt,
    DateTimeOffset? LastRequestAt,
    DateTimeOffset? LatestSuccessfulBackupAt,
    string LegacySourceDigest,
    long LegacySourceSizeBytes);

public sealed record CutoverReadinessReport(
    DateTimeOffset CapturedAt,
    string Version,
    CutoverInventory Inventory,
    IReadOnlyList<CutoverCheck> Checks)
{
    public bool IsReady => Checks.All(check => check.State != CutoverCheckState.Blocked);

    public int BlockedCount => Checks.Count(check => check.State == CutoverCheckState.Blocked);

    public int WarningCount => Checks.Count(check => check.State == CutoverCheckState.Warning);

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record LegacyStatisticsSnapshot(
    DateTimeOffset CapturedAt,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    long Requests,
    long PageViews,
    long Sessions,
    long Clients,
    long Errors)
{
    public static LegacyStatisticsSnapshot Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var snapshot = JsonSerializer.Deserialize<LegacyStatisticsSnapshot>(json, CutoverReadinessReport.JsonOptions) ??
            throw new InvalidOperationException("The legacy statistics snapshot is empty.");
        if (snapshot.WindowEnd <= snapshot.WindowStart)
        {
            throw new InvalidOperationException("The legacy statistics window end must be after its start.");
        }

        return snapshot;
    }
}

public sealed record CutoverStatistics(
    long Requests,
    long PageViews,
    long Sessions,
    long Clients,
    long Errors);

public sealed record CutoverMetricComparison(
    string Metric,
    long LegacyValue,
    long DotNetValue,
    long AbsoluteDifference,
    double DifferencePercent,
    bool WithinTolerance);

public sealed record CutoverComparisonReport(
    DateTimeOffset CapturedAt,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    double MaximumDifferencePercent,
    IReadOnlyList<CutoverMetricComparison> Metrics)
{
    public bool IsWithinTolerance => Metrics.All(metric => metric.WithinTolerance);

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, CutoverReadinessReport.JsonOptions);
    }
}

public static class CutoverStatisticsComparer
{
    public static CutoverComparisonReport Compare(
        LegacyStatisticsSnapshot legacy,
        CutoverStatistics dotNet,
        double maximumDifferencePercent,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(dotNet);
        maximumDifferencePercent = Math.Clamp(maximumDifferencePercent, 0, 100);

        return new CutoverComparisonReport(
            capturedAt,
            legacy.WindowStart,
            legacy.WindowEnd,
            maximumDifferencePercent,
            new[]
            {
                CompareMetric("requests", legacy.Requests, dotNet.Requests, maximumDifferencePercent),
                CompareMetric("pageViews", legacy.PageViews, dotNet.PageViews, maximumDifferencePercent),
                CompareMetric("sessions", legacy.Sessions, dotNet.Sessions, maximumDifferencePercent),
                CompareMetric("clients", legacy.Clients, dotNet.Clients, maximumDifferencePercent),
                CompareMetric("errors", legacy.Errors, dotNet.Errors, maximumDifferencePercent),
            });
    }

    private static CutoverMetricComparison CompareMetric(
        string metric,
        long legacyValue,
        long dotNetValue,
        double maximumDifferencePercent)
    {
        var difference = Math.Abs(dotNetValue - legacyValue);
        var denominator = Math.Max(Math.Abs(legacyValue), 1);
        var percent = difference * 100d / denominator;

        return new CutoverMetricComparison(
            metric,
            legacyValue,
            dotNetValue,
            difference,
            percent,
            percent <= maximumDifferencePercent);
    }
}
