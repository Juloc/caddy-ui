using System.Data;
using System.Data.Common;
using System.Globalization;

namespace CaddyUi.Migration;

public sealed partial class LegacyImportService
{
    private static async Task PreserveLegacyRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceDigest,
        string tableName,
        string sourceKey,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.legacy_source_rows(
                source_digest, table_name, source_key, payload_json, imported_at)
            VALUES(
                @source_digest, @table_name, @source_key,
                CAST(@payload_json AS jsonb), @imported_at)
            ON CONFLICT (source_digest, table_name, source_key) DO UPDATE SET
                payload_json = EXCLUDED.payload_json,
                imported_at = EXCLUDED.imported_at
            """,
            new Dictionary<string, object?>
            {
                ["source_digest"] = sourceDigest,
                ["table_name"] = tableName,
                ["source_key"] = sourceKey,
                ["payload_json"] = payloadJson,
                ["imported_at"] = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private static async Task InsertImportKeyAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceDigest,
        string tableName,
        string sourceKey,
        string targetTable,
        string targetKey,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.legacy_import_keys(
                source_digest, table_name, source_key,
                target_table, target_key, imported_at)
            VALUES(
                @source_digest, @table_name, @source_key,
                @target_table, @target_key, @imported_at)
            ON CONFLICT (source_digest, table_name, source_key) DO UPDATE SET
                target_table = EXCLUDED.target_table,
                target_key = EXCLUDED.target_key,
                imported_at = EXCLUDED.imported_at
            """,
            new Dictionary<string, object?>
            {
                ["source_digest"] = sourceDigest,
                ["table_name"] = tableName,
                ["source_key"] = sourceKey,
                ["target_table"] = targetTable,
                ["target_key"] = targetKey,
                ["imported_at"] = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private static async Task InsertMigrationRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        string sourcePath,
        string backupPath,
        LegacyDatabaseInventory inventory,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.migration_runs(
                id, source_path, source_digest, source_schema_version,
                source_size_bytes, started_at, status, dry_run, backup_path)
            VALUES(
                @id, @source_path, @source_digest, @source_schema_version,
                @source_size_bytes, @started_at, 'running', false, @backup_path)
            """,
            new Dictionary<string, object?>
            {
                ["id"] = runId,
                ["source_path"] = Path.GetFullPath(sourcePath),
                ["source_digest"] = inventory.SourceDigest,
                ["source_schema_version"] = inventory.SchemaVersion,
                ["source_size_bytes"] = inventory.SourceSizeBytes,
                ["started_at"] = startedAt,
                ["backup_path"] = backupPath
            },
            cancellationToken);
    }

    private static async Task InsertTableResultAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        LegacyTableMigrationResult result,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.migration_table_results(
                migration_run_id, table_name, source_rows, imported_rows,
                preserved_rows, skipped_rows, target_table, note)
            VALUES(
                @migration_run_id, @table_name, @source_rows, @imported_rows,
                @preserved_rows, @skipped_rows, @target_table, @note)
            """,
            new Dictionary<string, object?>
            {
                ["migration_run_id"] = runId,
                ["table_name"] = result.TableName,
                ["source_rows"] = result.SourceRows,
                ["imported_rows"] = result.ImportedRows,
                ["preserved_rows"] = result.PreservedRows,
                ["skipped_rows"] = result.SkippedRows,
                ["target_table"] = result.TargetTable,
                ["note"] = result.Note
            },
            cancellationToken);
    }

    private static async Task CompleteMigrationRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        LegacyMigrationReport report,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE caddy_ui.migration_runs
            SET completed_at = @completed_at,
                status = 'succeeded',
                report_json = CAST(@report_json AS jsonb)
            WHERE id = @id
            """,
            new Dictionary<string, object?>
            {
                ["completed_at"] = report.CompletedAt,
                ["report_json"] = report.ToJson(),
                ["id"] = runId
            },
            cancellationToken);
    }

    private static async Task<bool> HasSuccessfulImportAsync(
        DbConnection connection,
        string sourceDigest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM caddy_ui.migration_runs
                WHERE source_digest = @source_digest
                  AND status = 'succeeded')
            """;
        AddParameter(command, "source_digest", sourceDigest);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static async Task<Guid?> FindSuccessfulRunAsync(
        DbConnection connection,
        string sourceDigest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id
            FROM caddy_ui.migration_runs
            WHERE source_digest = @source_digest
              AND status = 'succeeded'
            ORDER BY completed_at DESC
            LIMIT 1
            """;
        AddParameter(command, "source_digest", sourceDigest);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid value ? value : null;
    }

    private static async Task<IReadOnlyDictionary<string, LegacyTableMigrationResult>>
        ReadTableResultsAsync(
            DbConnection connection,
            Guid runId,
            CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, LegacyTableMigrationResult>(
            StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT table_name, source_rows, imported_rows, preserved_rows,
                   skipped_rows, target_table, note
            FROM caddy_ui.migration_table_results
            WHERE migration_run_id = @migration_run_id
            """;
        AddParameter(command, "migration_run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var result = new LegacyTableMigrationResult(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6));
            results[result.TableName] = result;
        }

        return results;
    }

    private static async Task<long> CountImportKeysAsync(
        DbConnection connection,
        string sourceDigest,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM caddy_ui.legacy_import_keys
            WHERE source_digest = @source_digest
              AND table_name = @table_name
            """;
        AddParameter(command, "source_digest", sourceDigest);
        AddParameter(command, "table_name", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Key, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task EnsureOpenAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
    }
}
