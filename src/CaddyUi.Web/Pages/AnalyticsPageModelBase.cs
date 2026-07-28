using CaddyUi.Infrastructure.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages;

public abstract class AnalyticsPageModelBase : PageModel
{
    protected AnalyticsPageModelBase(
        AnalyticsReadStore store,
        TimeProvider timeProvider)
    {
        Store = store;
        TimeProvider = timeProvider;
    }

    protected AnalyticsReadStore Store { get; }

    protected TimeProvider TimeProvider { get; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "24h";

    [BindProperty(SupportsGet = true)]
    public string Host { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Actor { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Type { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 200;

    public IReadOnlyList<string> Hosts { get; private set; } = Array.Empty<string>();

    public AnalyticsReadFilter Filter { get; private set; } = AnalyticsReadFilter.Create(
        DateTimeOffset.UtcNow.AddHours(-24),
        DateTimeOffset.UtcNow);

    protected async Task<AnalyticsReadFilter> PrepareFilterAsync(
        CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();
        var from = NormalizeRange(Range, now);
        Filter = AnalyticsReadFilter.Create(
            from,
            now,
            Host,
            Actor,
            Type,
            Status,
            Limit);
        Range = NormalizeRangeName(Range);
        Host = Filter.Host;
        Actor = Filter.ActorType;
        Type = Filter.RequestType;
        Status = Filter.StatusClass;
        Limit = Filter.Limit;

        Hosts = await Store.ListHostsAsync(cancellationToken);
        if (Host.Length > 0 && !Hosts.Contains(Host, StringComparer.Ordinal))
        {
            Hosts = Hosts
                .Append(Host)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }

        return Filter;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024d:N1} KiB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024):N1} MiB";
        }

        return $"{bytes / (1024d * 1024 * 1024):N1} GiB";
    }

    public static string FormatDuration(double milliseconds)
    {
        return milliseconds < 1000
            ? $"{milliseconds:N1} ms"
            : $"{milliseconds / 1000:N2} s";
    }

    private static DateTimeOffset NormalizeRange(string? range, DateTimeOffset now)
    {
        return NormalizeRangeName(range) switch
        {
            "1h" => now.AddHours(-1),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            "90d" => now.AddDays(-90),
            _ => now.AddHours(-24),
        };
    }

    private static string NormalizeRangeName(string? range)
    {
        return range?.Trim().ToLowerInvariant() switch
        {
            "1h" => "1h",
            "7d" => "7d",
            "30d" => "30d",
            "90d" => "90d",
            _ => "24h",
        };
    }
}
