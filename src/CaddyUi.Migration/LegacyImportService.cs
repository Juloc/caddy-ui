using System.Data;
using System.Data.Common;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Migration;

public sealed partial class LegacyImportService
{
    private static readonly HashSet<string> EphemeralTables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "sessions",
            "portal_sessions",
            "route_previews"
        };

    private static readonly HashSet<string> RawRequestTables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "requests",
            "request_events",
            "access_logs",
            "access_log_events",
            "log_events"
        };

    private readonly CaddyUiDbContext _database;
    private readonly LegacySqliteReader _reader;
    private readonly IDataProtector _totpProtector;
    private readonly ILogger<LegacyImportService> _logger;

    public LegacyImportService(
        CaddyUiDbContext database,
        LegacySqliteReader reader,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<LegacyImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _database = database;
        _reader = reader;
        _totpProtector = dataProtectionProvider.CreateProtector(
            "CaddyUi.UserTotp.v1");
        _logger = logger;
    }

    public async Task<LegacyMigrationReport> PlanAsync(
        LegacyMigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTimeOffset.UtcNow;
        var inventory = await _reader.InspectAsync(
            options.SourcePath,
            cancellationToken);

        return CreatePlan(
            inventory,
            options.IncludeRawRequests,
            startedAt);
    }

    public static LegacyMigrationReport CreatePlan(
        LegacyDatabaseInventory inventory,
        bool includeRawRequests,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var results = inventory.Tables
            .OrderBy(GetImportOrder)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .Select(table => PlanTable(table, includeRawRequests))
            .ToArray();

        return new LegacyMigrationReport
        {
            Mode = "import",
            SourcePath = inventory.SourcePath,
            SourceDigest = inventory.SourceDigest,
            SourceSizeBytes = inventory.SourceSizeBytes,
            SourceSchemaVersion = inventory.SchemaVersion,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Status = "planned",
            DryRun = true,
            AlreadyImported = false,
            Tables = results,
            Warnings = BuildInventoryWarnings(inventory)
        };
    }

    public async Task<LegacyMigrationReport> ImportAsync(
        LegacyMigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.DryRun)
        {
            return await PlanAsync(options, cancellationToken);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var backupPath = _reader.CreateConsistentBackup(
            options.SourcePath,
            options.BackupDirectory,
            startedAt);
        var inventory = await _reader.InspectAsync(backupPath, cancellationToken);

        await _database.Database.MigrateAsync(cancellationToken);
        _ = _totpProtector.Protect("phase-two-key-initialization");
        var connection = _database.Database.GetDbConnection();
        await EnsureOpenAsync(connection, cancellationToken);

        if (await HasSuccessfulImportAsync(
                connection,
                inventory.SourceDigest,
                cancellationToken))
        {
            return new LegacyMigrationReport
            {
                Mode = "import",
                SourcePath = Path.GetFullPath(options.SourcePath),
                SourceDigest = inventory.SourceDigest,
                SourceSizeBytes = inventory.SourceSizeBytes,
                SourceSchemaVersion = inventory.SchemaVersion,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Status = "already-imported",
                DryRun = false,
                AlreadyImported = true,
                BackupPath = backupPath,
                Tables = Array.Empty<LegacyTableMigrationResult>(),
                Warnings =
                [
                    "A successful migration with the same consistent-backup digest already exists."
                ]
            };
        }

        var runId = Guid.NewGuid();
        var tableResults = new List<LegacyTableMigrationResult>();
        var warnings = BuildInventoryWarnings(inventory);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            await InsertMigrationRunAsync(
                connection,
                transaction,
                runId,
                options.SourcePath,
                backupPath,
                inventory,
                startedAt,
                cancellationToken);

            foreach (var table in inventory.Tables
                         .OrderBy(GetImportOrder)
                         .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = await ImportTableAsync(
                    connection,
                    transaction,
                    backupPath,
                    inventory.SourceDigest,
                    table,
                    options.IncludeRawRequests,
                    cancellationToken);

                tableResults.Add(result);
                await InsertTableResultAsync(
                    connection,
                    transaction,
                    runId,
                    result,
                    cancellationToken);
            }

            var report = new LegacyMigrationReport
            {
                Mode = "import",
                SourcePath = Path.GetFullPath(options.SourcePath),
                SourceDigest = inventory.SourceDigest,
                SourceSizeBytes = inventory.SourceSizeBytes,
                SourceSchemaVersion = inventory.SchemaVersion,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Status = "succeeded",
                DryRun = false,
                AlreadyImported = false,
                BackupPath = backupPath,
                MigrationRunId = runId,
                Tables = tableResults,
                Warnings = warnings
            };

            await CompleteMigrationRunAsync(
                connection,
                transaction,
                runId,
                report,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Imported legacy SQLite database {SourcePath} as migration run {MigrationRunId}.",
                options.SourcePath,
                runId);

            return report;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(
                exception,
                "Legacy SQLite import failed for {SourcePath}.",
                options.SourcePath);
            throw;
        }
    }

    public async Task<LegacyMigrationReport> VerifyAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var startedAt = DateTimeOffset.UtcNow;
        var inventory = await _reader.InspectAsync(sourcePath, cancellationToken);

        await _database.Database.MigrateAsync(cancellationToken);
        var connection = _database.Database.GetDbConnection();
        await EnsureOpenAsync(connection, cancellationToken);

        var runId = await FindSuccessfulRunAsync(
            connection,
            inventory.SourceDigest,
            cancellationToken);

        if (runId is null)
        {
            return new LegacyMigrationReport
            {
                Mode = "verify",
                SourcePath = inventory.SourcePath,
                SourceDigest = inventory.SourceDigest,
                SourceSizeBytes = inventory.SourceSizeBytes,
                SourceSchemaVersion = inventory.SchemaVersion,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Status = "not-imported",
                DryRun = false,
                AlreadyImported = false,
                Tables = Array.Empty<LegacyTableMigrationResult>(),
                Warnings =
                [
                    "No successful migration run matches this SQLite digest. Verify the consistent backup created during import."
                ]
            };
        }

        var storedResults = await ReadTableResultsAsync(
            connection,
            runId.Value,
            cancellationToken);
        var warnings = new List<string>();
        var verifiedResults = new List<LegacyTableMigrationResult>();

        foreach (var table in inventory.Tables)
        {
            if (!storedResults.TryGetValue(table.Name, out var stored))
            {
                warnings.Add($"No migration result exists for table '{table.Name}'.");
                continue;
            }

            var accounted = stored.ImportedRows + stored.PreservedRows + stored.SkippedRows;
            if (stored.SourceRows != table.RowCount || accounted != table.RowCount)
            {
                warnings.Add(
                    $"Table '{table.Name}' has {table.RowCount} source rows, but the migration report accounts for {accounted}.");
            }

            var keyCount = await CountImportKeysAsync(
                connection,
                inventory.SourceDigest,
                table.Name,
                cancellationToken);
            var expectedKeys = stored.ImportedRows + stored.PreservedRows;
            if (keyCount != expectedKeys)
            {
                warnings.Add(
                    $"Table '{table.Name}' expects {expectedKeys} import keys, but PostgreSQL contains {keyCount}.");
            }

            verifiedResults.Add(stored);
        }

        return new LegacyMigrationReport
        {
            Mode = "verify",
            SourcePath = inventory.SourcePath,
            SourceDigest = inventory.SourceDigest,
            SourceSizeBytes = inventory.SourceSizeBytes,
            SourceSchemaVersion = inventory.SchemaVersion,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Status = warnings.Count == 0 ? "verified" : "verification-failed",
            DryRun = false,
            AlreadyImported = true,
            MigrationRunId = runId,
            Tables = verifiedResults,
            Warnings = warnings
        };
    }

    private async Task<LegacyTableMigrationResult> ImportTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourcePath,
        string sourceDigest,
        LegacyTableInventory table,
        bool includeRawRequests,
        CancellationToken cancellationToken)
    {
        if (EphemeralTables.Contains(table.Name))
        {
            return new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                0,
                0,
                table.RowCount,
                string.Empty,
                "Active admin sessions, portal sessions, and route previews are intentionally not migrated.");
        }

        if (RawRequestTables.Contains(table.Name) && !includeRawRequests)
        {
            return new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                0,
                0,
                table.RowCount,
                string.Empty,
                "Raw request rows were excluded by configuration.");
        }

        long importedRows = 0;
        long preservedRows = 0;
        long rowNumber = 0;
        var targetTable = TargetTableFor(table.Name);

        await foreach (var row in _reader.ReadRowsAsync(
                           sourcePath,
                           table,
                           cancellationToken))
        {
            rowNumber++;
            var payloadJson = SerializeRow(row);
            var sourceKey = BuildSourceKey(table, row, rowNumber, payloadJson);

            ImportOutcome outcome;
            if (targetTable is null)
            {
                await PreserveLegacyRowAsync(
                    connection,
                    transaction,
                    sourceDigest,
                    table.Name,
                    sourceKey,
                    payloadJson,
                    cancellationToken);
                outcome = new ImportOutcome(
                    "legacy_source_rows",
                    sourceKey);
                preservedRows++;
            }
            else
            {
                outcome = await ImportKnownRowAsync(
                    connection,
                    transaction,
                    sourceDigest,
                    table.Name,
                    row,
                    cancellationToken);
                importedRows++;
            }

            await InsertImportKeyAsync(
                connection,
                transaction,
                sourceDigest,
                table.Name,
                sourceKey,
                outcome.TargetTable,
                outcome.TargetKey,
                cancellationToken);
        }

        return new LegacyTableMigrationResult(
            table.Name,
            table.RowCount,
            importedRows,
            preservedRows,
            0,
            targetTable ?? "legacy_source_rows",
            targetTable is null
                ? "The table is not yet modeled and was preserved row-for-row as JSON."
                : "Rows were mapped to the phase-2 PostgreSQL schema.");
    }

    private async Task<ImportOutcome> ImportKnownRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceDigest,
        string tableName,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        return tableName.ToLowerInvariant() switch
        {
            "users" => await ImportUserAsync(
                connection,
                transaction,
                row,
                _totpProtector,
                cancellationToken),
            "settings" => await ImportSettingAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "providers" => await ImportProviderAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "routes" => await ImportRouteAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "access_groups" => await ImportAccessGroupAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "access_credentials" => await ImportAccessCredentialAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "revisions" => await ImportRevisionAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "audit_events" => await ImportAuditEventAsync(
                connection,
                transaction,
                sourceDigest,
                row,
                cancellationToken),
            "notifications" => await ImportNotificationAsync(
                connection,
                transaction,
                sourceDigest,
                row,
                cancellationToken),
            "traffic_buckets" => await ImportTrafficBucketAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            "migration_state" => await ImportMigrationStateAsync(
                connection,
                transaction,
                row,
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Table '{tableName}' does not have a known importer.")
        };
    }
}
