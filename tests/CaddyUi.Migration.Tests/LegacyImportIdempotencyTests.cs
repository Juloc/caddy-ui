using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace CaddyUi.Migration.Tests;

public sealed class LegacyImportIdempotencyTests : IAsyncLifetime, IDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_migration_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_tests")
        .Build();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "caddy-ui-import-tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return _postgres.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _postgres.DisposeAsync().AsTask();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_IsIdempotentAndVerifiable()
    {
        var sourcePath = Path.Combine(_directory, "legacy.db");
        CreateLegacyDatabase(sourcePath);

        var options = new DbContextOptionsBuilder<CaddyUiDbContext>()
            .UseNpgsql(
                _postgres.GetConnectionString(),
                postgres => postgres.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "public"))
            .Options;
        await using var database = new CaddyUiDbContext(options);
        await database.Database.MigrateAsync();

        var dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_directory, "data-protection-keys")));
        var service = new LegacyImportService(
            database,
            new LegacySqliteReader(),
            dataProtectionProvider,
            NullLogger<LegacyImportService>.Instance);
        var migrationOptions = new LegacyMigrationOptions(
            sourcePath,
            Path.Combine(_directory, "backups"),
            ReportPath: null,
            DryRun: false,
            IncludeRawRequests: false);

        var first = await service.ImportAsync(migrationOptions);
        var second = await service.ImportAsync(migrationOptions);

        Assert.Equal("succeeded", first.Status);
        Assert.False(first.AlreadyImported);
        Assert.NotNull(first.BackupPath);
        Assert.Equal("already-imported", second.Status);
        Assert.True(second.AlreadyImported);

        var verification = await service.VerifyAsync(first.BackupPath!);

        Assert.Equal("verified", verification.Status);
        Assert.Empty(verification.Warnings);
        Assert.All(
            first.Tables,
            table => Assert.Equal(
                table.SourceRows,
                table.ImportedRows + table.PreservedRows + table.SkippedRows));

        Assert.Equal(
            1,
            await database.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*)::integer AS \"Value\" FROM caddy_ui.users")
                .SingleAsync());
        Assert.Equal(
            1,
            await database.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*)::integer AS \"Value\" FROM caddy_ui.legacy_source_rows")
                .SingleAsync());
        Assert.Equal(
            0,
            await database.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*)::integer AS \"Value\" FROM caddy_ui.admin_sessions")
                .SingleAsync());
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
            CREATE TABLE unknown_feature(
                id INTEGER PRIMARY KEY,
                payload TEXT NOT NULL
            );
            INSERT INTO users VALUES(
                '11111111-1111-1111-1111-111111111111',
                'admin',
                'Administrator',
                'legacy-hash',
                'admin',
                1,
                'LEGACY-TOTP',
                1,
                'dark',
                '2026-07-27T00:00:00+00:00',
                '2026-07-27T00:00:00+00:00',
                NULL
            );
            INSERT INTO settings VALUES(
                'accent',
                '"purple"',
                '2026-07-27T00:00:00+00:00'
            );
            INSERT INTO sessions VALUES(
                'session-hash',
                '11111111-1111-1111-1111-111111111111'
            );
            INSERT INTO unknown_feature VALUES(1, '{"enabled":true}');
            """;
        command.ExecuteNonQuery();
    }
}
