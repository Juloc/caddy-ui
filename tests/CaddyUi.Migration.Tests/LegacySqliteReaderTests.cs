using Microsoft.Data.Sqlite;

namespace CaddyUi.Migration.Tests;

public sealed class LegacySqliteReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "caddy-ui-migration-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAndBackup_PreserveInventoryAndSchemaVersion()
    {
        Directory.CreateDirectory(_directory);
        var sourcePath = Path.Combine(_directory, "legacy.db");
        CreateLegacyDatabase(sourcePath);

        var reader = new LegacySqliteReader();
        var inventory = await reader.InspectAsync(sourcePath);

        Assert.Equal(2, inventory.SchemaVersion);
        Assert.Equal(4, inventory.Tables.Count);
        Assert.Contains(
            inventory.Tables,
            table => table.Name == "users" &&
                table.RowCount == 1 &&
                table.PrimaryKeyColumns.SequenceEqual(["id"]));
        Assert.Equal(64, inventory.SourceDigest.Length);

        var backupDirectory = Path.Combine(_directory, "backups");
        var backupPath = reader.CreateConsistentBackup(
            sourcePath,
            backupDirectory,
            DateTimeOffset.Parse(
                "2026-07-28T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture));

        var backupInventory = await reader.InspectAsync(backupPath);

        Assert.Equal(inventory.SchemaVersion, backupInventory.SchemaVersion);
        Assert.Equal(
            inventory.Tables.Select(table => (table.Name, table.RowCount)),
            backupInventory.Tables.Select(table => (table.Name, table.RowCount)));
    }

    private static void CreateLegacyDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA user_version=2;
            CREATE TABLE users(
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                display_name TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                totp_secret TEXT NOT NULL,
                totp_enabled INTEGER NOT NULL,
                theme TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_login_at TEXT
            );
            CREATE TABLE settings(
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE sessions(
                token_hash TEXT PRIMARY KEY,
                user_id TEXT NOT NULL
            );
            CREATE TABLE future_table(
                id INTEGER PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO users VALUES(
                '11111111-1111-1111-1111-111111111111',
                'admin',
                'Administrator',
                'legacy-hash',
                'admin',
                1,
                '',
                0,
                'system',
                '2026-07-27T00:00:00+00:00',
                '2026-07-27T00:00:00+00:00',
                NULL
            );
            INSERT INTO settings VALUES(
                'accent',
                '"blue"',
                '2026-07-27T00:00:00+00:00'
            );
            INSERT INTO sessions VALUES('session-hash', '11111111-1111-1111-1111-111111111111');
            INSERT INTO future_table VALUES(1, 'preserve me');
            """;
        command.ExecuteNonQuery();
    }
}
