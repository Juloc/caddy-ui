using System.Text.Json;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class BackupOperationsStoreTests : IAsyncLifetime
{
    private static readonly string[] BackupPersistenceMethods =
    [
        "ListBackupsAsync",
        "RecordBackupAsync",
    ];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_backup_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_backup_tests")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public void BackupOperationsStore_ReplacesLegacyAggregateStore()
    {
        var assembly = typeof(BackupOperationsStore).Assembly;
        Assert.Null(assembly.GetType("CaddyUi.Infrastructure.Operations.OperationsStore"));

        var backupMethods = typeof(BackupOperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in BackupPersistenceMethods)
        {
            Assert.Contains(method, backupMethods);
        }
    }

    [Fact]
    public async Task BackupArtifacts_PersistAndListNewestFirst()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var store = new BackupOperationsStore(factory);
        var first = new BackupArtifactRecord(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero),
            "first.zip",
            "/backups/first.zip",
            100,
            "digest-first",
            "ok",
            string.Empty,
            "{\"schema\":\"first\"}");
        var second = new BackupArtifactRecord(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero),
            "second.zip",
            "/backups/second.zip",
            200,
            "digest-second",
            "failed",
            "pg_dump failed",
            "{\"schema\":\"second\",\"includes\":[\"database\"]}");

        await store.RecordBackupAsync(first);
        await store.RecordBackupAsync(second);

        var backups = await store.ListBackupsAsync();
        Assert.Equal(2, backups.Count);
        Assert.Equal(second.Id, backups[0].Id);
        Assert.Equal(first.Id, backups[1].Id);
        Assert.Equal("failed", backups[0].Status);
        Assert.Equal("pg_dump failed", backups[0].Error);
        Assert.Equal(200, backups[0].SizeBytes);

        using var manifest = JsonDocument.Parse(backups[0].ManifestJson);
        Assert.Equal("second", manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal("database", manifest.RootElement.GetProperty("includes")[0].GetString());
    }

    [Fact]
    public async Task RecordBackup_RejectsNonObjectManifest()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var store = new BackupOperationsStore(factory);
        var artifact = new BackupArtifactRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "invalid.zip",
            "/backups/invalid.zip",
            0,
            string.Empty,
            "failed",
            "invalid manifest",
            "[]");

        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordBackupAsync(artifact));
    }
}
