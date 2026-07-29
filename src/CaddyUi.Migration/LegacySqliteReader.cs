using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CaddyUi.Migration;

public sealed class LegacySqliteReader
{
    public async Task<LegacyDatabaseInventory> InspectAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The legacy SQLite database does not exist.", fullPath);
        }

        var digest = await ComputeDigestAsync(fullPath, cancellationToken);
        var fileInfo = new FileInfo(fullPath);
        var tables = new List<LegacyTableInventory>();

        await using var connection = CreateReadOnlyConnection(fullPath);
        await connection.OpenAsync(cancellationToken);

        var schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
        var tableNames = await ReadTableNamesAsync(connection, cancellationToken);

        foreach (var tableName in tableNames)
        {
            var columns = await ReadColumnsAsync(connection, tableName, cancellationToken);
            var rowCount = await ReadRowCountAsync(connection, tableName, cancellationToken);

            tables.Add(new LegacyTableInventory(
                tableName,
                rowCount,
                columns.Select(column => column.Name).ToArray(),
                columns
                    .Where(column => column.PrimaryKeyOrder > 0)
                    .OrderBy(column => column.PrimaryKeyOrder)
                    .Select(column => column.Name)
                    .ToArray()));
        }

        return new LegacyDatabaseInventory(
            fullPath,
            digest,
            fileInfo.Length,
            schemaVersion,
            tables);
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadRowsAsync(
        string sourcePath,
        LegacyTableInventory table,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(table);

        await using var connection = CreateReadOnlyConnection(Path.GetFullPath(sourcePath));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)}";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(
                reader.FieldCount,
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] =
                    await reader.IsDBNullAsync(index, cancellationToken)
                        ? null
                        : reader.GetValue(index);
            }

            yield return values;
        }
    }

    public string CreateConsistentBackup(
        string sourcePath,
        string backupDirectory,
        DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException(
                "The legacy SQLite database does not exist.",
                fullSourcePath);
        }

        var fullBackupDirectory = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(fullBackupDirectory);

        var safeName = Path.GetFileNameWithoutExtension(fullSourcePath);
        var backupPath = Path.Combine(
            fullBackupDirectory,
            $"{safeName}-{timestamp:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db");

        using var source = CreateReadOnlyConnection(fullSourcePath);
        using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString());

        source.Open();
        destination.Open();
        source.BackupDatabase(destination);

        return backupPath;
    }

    public static async Task<string> ComputeDigestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    private static SqliteConnection CreateReadOnlyConnection(string path)
    {
        return new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString());
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<LegacyColumn>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<LegacyColumn>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new LegacyColumn(
                reader.GetString(1),
                reader.GetInt32(5)));
        }

        return columns;
    }

    private static async Task<long> ReadRowCountAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record LegacyColumn(string Name, int PrimaryKeyOrder);
}
