using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Cutover;

public sealed class CutoverReadinessService
{
    private readonly IDbContextFactory<CaddyUiDbContext> _databaseFactory;
    private readonly AnalyticsIngestionOptions _analytics;
    private readonly RoutingOptions _routing;
    private readonly OperationsOptions _operations;
    private readonly IpSecurityOptions _ipSecurity;
    private readonly CutoverOptions _options;
    private readonly TimeProvider _timeProvider;

    public CutoverReadinessService(
        IDbContextFactory<CaddyUiDbContext> databaseFactory,
        AnalyticsIngestionOptions analytics,
        RoutingOptions routing,
        OperationsOptions operations,
        IpSecurityOptions ipSecurity,
        CutoverOptions options,
        TimeProvider timeProvider)
    {
        _databaseFactory = databaseFactory;
        _analytics = analytics;
        _routing = routing;
        _operations = operations;
        _ipSecurity = ipSecurity;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<CutoverReadinessReport> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var checks = new List<CutoverCheck>();
        var inventory = new CutoverInventory(0, 0, 0, 0, 0, 0, 0, null, null, null, string.Empty, 0);

        checks.Add(Check(
            "cutover.explicit-enable",
            "Explizite Freigabe",
            _options.Enabled ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
            _options.Enabled
                ? "Der Phase-9-Cutover ist explizit freigegeben."
                : "Der Cutover ist standardmäßig deaktiviert.",
            "CADDY_UI_CUTOVER_ENABLED=true nur für das geplante Wartungsfenster setzen."));

        var databaseReady = false;
        await using (var database = await _databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            try
            {
                databaseReady = await database.Database.CanConnectAsync(cancellationToken);
                checks.Add(Check(
                    "postgres.connectivity",
                    "PostgreSQL",
                    databaseReady ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
                    databaseReady ? "PostgreSQL ist erreichbar." : "PostgreSQL ist nicht erreichbar.",
                    "Verbindungszeichenfolge und Datenbankstatus prüfen."));

                if (databaseReady)
                {
                    var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
                    checks.Add(Check(
                        "postgres.migrations",
                        "Migrationen",
                        pending.Length == 0 ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
                        pending.Length == 0
                            ? "Alle EF-Core-Migrationen sind angewendet."
                            : $"Ausstehende Migrationen: {string.Join(", ", pending)}",
                        "Vor dem Cutover `dotnet CaddyUi.Migration.dll schema` ausführen."));

                    inventory = await ReadInventoryAsync(database, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(Check(
                    "postgres.readiness-error",
                    "PostgreSQL-Prüfung",
                    CutoverCheckState.Blocked,
                    Limit(exception.Message),
                    "Datenbanklogs und Migrationen prüfen."));
            }
        }

        var legacy = await InspectLegacySourceAsync(cancellationToken);
        inventory = inventory with
        {
            LegacySourceDigest = legacy.Digest,
            LegacySourceSizeBytes = legacy.SizeBytes,
        };
        checks.Add(legacy.Check);

        checks.Add(Check(
            "analytics.enabled",
            "Shadow-Ingestion",
            _analytics.Enabled ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
            _analytics.Enabled
                ? "Die .NET-Ingestion verarbeitet die gemeinsamen Caddy-Logs."
                : "Die .NET-Ingestion ist deaktiviert.",
            "CADDY_UI_ANALYTICS_ENABLED=true im Shadow-Container setzen."));

        var readableLogs = _analytics.LogPaths.Count(path => File.Exists(path));
        checks.Add(Check(
            "analytics.shared-logs",
            "Gemeinsame Access-Logs",
            _analytics.LogPaths.Count > 0 && readableLogs == _analytics.LogPaths.Count
                ? CutoverCheckState.Passed
                : CutoverCheckState.Blocked,
            _analytics.LogPaths.Count == 0
                ? "Es sind keine Caddy-Logpfade konfiguriert."
                : $"Lesbar: {readableLogs} von {_analytics.LogPaths.Count} konfigurierten Logdateien.",
            "Dieselben Caddy-JSON-Logs read-only in den .NET-Container einhängen."));

        var observedHours = inventory.FirstRequestAt.HasValue && inventory.LastRequestAt.HasValue
            ? (inventory.LastRequestAt.Value - inventory.FirstRequestAt.Value).TotalHours
            : 0;
        checks.Add(Check(
            "analytics.shadow-duration",
            "Shadow-Laufzeit",
            observedHours >= _options.MinimumShadowHours
                ? CutoverCheckState.Passed
                : CutoverCheckState.Blocked,
            $"Beobachteter Zeitraum: {observedHours:F1} h; erforderlich: {_options.MinimumShadowHours} h.",
            "Shadow-Betrieb weiterlaufen lassen und danach erneut prüfen."));

        var ingestionAge = inventory.LastRequestAt.HasValue
            ? now - inventory.LastRequestAt.Value
            : TimeSpan.MaxValue;
        checks.Add(Check(
            "analytics.freshness",
            "Ingestion-Aktualität",
            ingestionAge <= TimeSpan.FromMinutes(15)
                ? CutoverCheckState.Passed
                : CutoverCheckState.Blocked,
            inventory.LastRequestAt.HasValue
                ? $"Letzter Request vor {ingestionAge.TotalMinutes:F1} Minuten."
                : "Noch keine Requests in PostgreSQL.",
            "Logpfade, Tailer-Checkpoint und Containerzeit prüfen."));

        checks.Add(CreateModeCheck(
            "routing.mode",
            "Routen-Schreibmodus",
            _routing.WriteMode == RouteWriteMode.Active,
            _routing.WriteMode.ToString()));
        checks.Add(CreateModeCheck(
            "dns.mode",
            "DNS-Schreibmodus",
            _operations.DnsWriteMode == OperationsWriteMode.Active,
            _operations.DnsWriteMode.ToString()));
        checks.Add(CreateModeCheck(
            "blocklist.mode",
            "Blocklist-Schreibmodus",
            _ipSecurity.BlockWriteMode == IpBlockWriteMode.Active,
            _ipSecurity.BlockWriteMode.ToString()));

        checks.Add(Check(
            "operations.worker",
            "Betriebsworker",
            !_operations.WorkerEnabled ? CutoverCheckState.Passed : CutoverCheckState.Warning,
            _operations.WorkerEnabled
                ? "Der Betriebsworker ist bereits aktiv."
                : "Der Betriebsworker bleibt bis zur Umschaltung deaktiviert.",
            "Worker erst nach erfolgreicher Portumschaltung aktivieren."));

        checks.Add(Check(
            "inventory.admin-user",
            "Administratorkonto",
            inventory.Users > 0 ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
            $"Importierte Benutzer: {inventory.Users}.",
            "Finalen Legacy-Import prüfen und mindestens ein Administratorkonto sicherstellen."));
        checks.Add(Check(
            "inventory.domains",
            "Domains",
            inventory.Domains > 0 ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
            $"Importierte Domains: {inventory.Domains}.",
            "Domainimport und Providerzuordnung prüfen."));
        checks.Add(Check(
            "inventory.routes",
            "Routen",
            inventory.Routes > 0 ? CutoverCheckState.Passed : CutoverCheckState.Warning,
            $"Verwaltete Routen: {inventory.Routes}.",
            "Bei erwarteten Routen den Import und die Preview prüfen."));

        var backupAge = inventory.LatestSuccessfulBackupAt.HasValue
            ? now - inventory.LatestSuccessfulBackupAt.Value
            : TimeSpan.MaxValue;
        checks.Add(Check(
            "backup.recent",
            "Aktuelles PostgreSQL-Backup",
            backupAge <= TimeSpan.FromHours(_options.MaximumBackupAgeHours)
                ? CutoverCheckState.Passed
                : CutoverCheckState.Blocked,
            inventory.LatestSuccessfulBackupAt.HasValue
                ? $"Letztes erfolgreiches Backup vor {backupAge.TotalHours:F1} h."
                : "Es existiert kein erfolgreiches PostgreSQL-Backup.",
            "Unmittelbar vor dem Wartungsfenster ein Backup erstellen und testweise prüfen."));

        checks.Add(CheckManifestDirectory());
        checks.Add(Check(
            "ports.distinct",
            "Admin- und Portalport",
            _options.AdminPort != _options.PortalPort ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
            $"Admin :{_options.AdminPort}, Portal :{_options.PortalPort}.",
            "Getrennte Ports konfigurieren."));

        var comparison = await TryCompareConfiguredSnapshotAsync(cancellationToken);
        checks.Add(comparison.Check);

        return new CutoverReadinessReport(
            now,
            Environment.GetEnvironmentVariable("CADDY_UI_VERSION") ?? "development",
            inventory,
            checks);
    }

    public async Task<CutoverComparisonReport> CompareConfiguredSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(_options.LegacyStatisticsPath, cancellationToken);
        var legacy = LegacyStatisticsSnapshot.Parse(json);
        var dotNet = await ReadStatisticsAsync(legacy.WindowStart, legacy.WindowEnd, cancellationToken);
        return CutoverStatisticsComparer.Compare(
            legacy,
            dotNet,
            _options.MaximumMetricDifferencePercent,
            _timeProvider.GetUtcNow());
    }

    public async Task<string> WriteReadinessManifestAsync(
        CutoverReadinessReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(_options.ManifestDirectory);
        var digest = string.IsNullOrWhiteSpace(report.Inventory.LegacySourceDigest)
            ? "no-source"
            : report.Inventory.LegacySourceDigest[..Math.Min(12, report.Inventory.LegacySourceDigest.Length)];
        var path = Path.Combine(
            _options.ManifestDirectory,
            $"readiness-{report.CapturedAt:yyyyMMddTHHmmssZ}-{digest}.json");
        await WriteImmutableAsync(path, report.ToJson(), cancellationToken);
        return path;
    }

    public async Task<string> WriteComparisonManifestAsync(
        CutoverComparisonReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(_options.ManifestDirectory);
        var path = Path.Combine(
            _options.ManifestDirectory,
            $"statistics-{report.CapturedAt:yyyyMMddTHHmmssZ}.json");
        await WriteImmutableAsync(path, report.ToJson(), cancellationToken);
        return path;
    }

    private async Task<CutoverInventory> ReadInventoryAsync(
        CaddyUiDbContext database,
        CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = database.Database.GetDbConnection();
            return new CutoverInventory(
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.users", cancellationToken),
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.managed_domains", cancellationToken),
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.managed_routes", cancellationToken),
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.request_events", cancellationToken),
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.page_views WHERE successful", cancellationToken),
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.analytics_sessions", cancellationToken),
                await ScalarInt64Async(connection, "SELECT COUNT(*) FROM caddy_ui.anonymous_clients", cancellationToken),
                await ScalarTimestampAsync(connection, "SELECT MIN(occurred_at) FROM caddy_ui.request_events", cancellationToken),
                await ScalarTimestampAsync(connection, "SELECT MAX(occurred_at) FROM caddy_ui.request_events", cancellationToken),
                await ScalarTimestampAsync(
                    connection,
                    "SELECT MAX(created_at) FROM caddy_ui.backup_artifacts WHERE status = 'ok'",
                    cancellationToken),
                string.Empty,
                0);
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }

    private async Task<CutoverStatistics> ReadStatisticsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var database = await _databaseFactory.CreateDbContextAsync(cancellationToken);
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = database.Database.GetDbConnection();
            return new CutoverStatistics(
                await ScalarWindowAsync(connection, "SELECT COUNT(*) FROM caddy_ui.request_events WHERE occurred_at >= @from AND occurred_at < @to", from, to, cancellationToken),
                await ScalarWindowAsync(connection, "SELECT COUNT(*) FROM caddy_ui.page_views WHERE successful AND occurred_at >= @from AND occurred_at < @to", from, to, cancellationToken),
                await ScalarWindowAsync(connection, "SELECT COUNT(*) FROM caddy_ui.analytics_sessions WHERE started_at >= @from AND started_at < @to", from, to, cancellationToken),
                await ScalarWindowAsync(connection, "SELECT COUNT(DISTINCT anonymous_client_id) FROM caddy_ui.request_events WHERE anonymous_client_id IS NOT NULL AND occurred_at >= @from AND occurred_at < @to", from, to, cancellationToken),
                await ScalarWindowAsync(connection, "SELECT COUNT(*) FROM caddy_ui.request_events WHERE status >= 500 AND occurred_at >= @from AND occurred_at < @to", from, to, cancellationToken));
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }

    private async Task<(CutoverCheck Check, CutoverComparisonReport? Report)> TryCompareConfiguredSnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.LegacyStatisticsPath))
        {
            return (
                Check(
                    "statistics.snapshot",
                    "Statistikvergleich",
                    CutoverCheckState.Blocked,
                    $"Legacy-Snapshot fehlt: {_options.LegacyStatisticsPath}",
                    "Legacy-Zahlen für dasselbe UTC-Zeitfenster als JSON exportieren."),
                null);
        }

        try
        {
            var report = await CompareConfiguredSnapshotAsync(cancellationToken);
            return (
                Check(
                    "statistics.comparison",
                    "Statistikvergleich",
                    report.IsWithinTolerance ? CutoverCheckState.Passed : CutoverCheckState.Blocked,
                    report.IsWithinTolerance
                        ? $"Alle Kennzahlen liegen innerhalb von {report.MaximumDifferencePercent:F1} %."
                        : "Mindestens eine Kennzahl überschreitet die erlaubte Abweichung.",
                    "Zeitfenster, Klassifikation und Legacy-Export prüfen."),
                report);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (
                Check(
                    "statistics.comparison-error",
                    "Statistikvergleich",
                    CutoverCheckState.Blocked,
                    Limit(exception.Message),
                    "Snapshotformat und PostgreSQL-Daten prüfen."),
                null);
        }
    }

    private async Task<(CutoverCheck Check, string Digest, long SizeBytes)> InspectLegacySourceAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.LegacySqlitePath))
        {
            return (
                Check(
                    "legacy.sqlite",
                    "Legacy-SQLite",
                    CutoverCheckState.Blocked,
                    $"Legacy-Datei fehlt: {_options.LegacySqlitePath}",
                    "SQLite-Datei read-only in den .NET-Container einhängen."),
                string.Empty,
                0);
        }

        try
        {
            var file = new FileInfo(_options.LegacySqlitePath);
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            return (
                Check(
                    "legacy.sqlite",
                    "Legacy-SQLite",
                    CutoverCheckState.Passed,
                    $"Read-only Quelle lesbar, {file.Length} Bytes, SHA-256 {digest[..12]}…"),
                digest,
                file.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (
                Check(
                    "legacy.sqlite",
                    "Legacy-SQLite",
                    CutoverCheckState.Blocked,
                    Limit(exception.Message),
                    "Dateirechte und Mount prüfen."),
                string.Empty,
                0);
        }
    }

    private CutoverCheck CheckManifestDirectory()
    {
        try
        {
            Directory.CreateDirectory(_options.ManifestDirectory);
            var probe = Path.Combine(_options.ManifestDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe", Encoding.UTF8);
            File.Delete(probe);
            return Check(
                "cutover.manifest-directory",
                "Cutover-Manifeste",
                CutoverCheckState.Passed,
                $"Verzeichnis ist beschreibbar: {_options.ManifestDirectory}");
        }
        catch (Exception exception)
        {
            return Check(
                "cutover.manifest-directory",
                "Cutover-Manifeste",
                CutoverCheckState.Blocked,
                Limit(exception.Message),
                "Persistentes, nur für Caddy UI beschreibbares Verzeichnis konfigurieren.");
        }
    }

    private static CutoverCheck CreateModeCheck(
        string code,
        string title,
        bool active,
        string value)
    {
        return Check(
            code,
            title,
            active ? CutoverCheckState.Blocked : CutoverCheckState.Passed,
            $"Aktueller Modus: {value.ToLowerInvariant()}.",
            "Vor der Umschaltung disabled oder shadow verwenden; active erst nach Abnahme setzen.");
    }

    private static CutoverCheck Check(
        string code,
        string title,
        CutoverCheckState state,
        string detail,
        string remediation = "")
    {
        return new CutoverCheck(code, title, state, detail, remediation);
    }

    private static async Task<long> ScalarInt64Async(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset?> ScalarTimestampAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            null or DBNull => null,
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal),
        };
    }

    private static async Task<long> ScalarWindowAsync(
        DbConnection connection,
        string sql,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "from", from);
        AddParameter(command, "to", to);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task WriteImmutableAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
        try
        {
            File.Move(temporary, fullPath, overwrite: false);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private static string Limit(string value)
    {
        const int maximum = 500;
        return value.Length <= maximum ? value : value[..maximum];
    }
}
