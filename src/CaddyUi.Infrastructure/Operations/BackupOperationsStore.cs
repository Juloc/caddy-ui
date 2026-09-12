using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static CaddyUi.Infrastructure.Persistence.RelationalStoreSupport;

namespace CaddyUi.Infrastructure.Operations;

public sealed class BackupOperationsStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public BackupOperationsStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<BackupArtifactRecord>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, file_name, path, size_bytes, digest,
                   status, error, manifest_json::text
            FROM caddy_ui.backup_artifacts
            ORDER BY created_at DESC
            LIMIT 200
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BackupArtifactRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new BackupArtifactRecord(
                reader.GetGuid(0), ReadTimestamp(reader, 1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        }

        return result;
    }

    public Task RecordBackupAsync(BackupArtifactRecord artifact, CancellationToken cancellationToken = default)
    {
        var manifestJson = NormalizeObjectJson(artifact.ManifestJson);
        return ExecuteNonQueryAsync(
            _contextFactory,
            """
            INSERT INTO caddy_ui.backup_artifacts(
                id, created_at, file_name, path, size_bytes, digest, status, error, manifest_json)
            VALUES(@id, @created_at, @file_name, @path, @size_bytes, @digest,
                   @status, @error, CAST(@manifest_json AS jsonb))
            """,
            command =>
            {
                AddParameter(command, "id", artifact.Id);
                AddParameter(command, "created_at", artifact.CreatedAt);
                AddParameter(command, "file_name", artifact.FileName);
                AddParameter(command, "path", artifact.Path);
                AddParameter(command, "size_bytes", artifact.SizeBytes);
                AddParameter(command, "digest", artifact.Digest);
                AddParameter(command, "status", artifact.Status);
                AddParameter(command, "error", artifact.Error);
                AddParameter(command, "manifest_json", manifestJson);
            },
            cancellationToken);
    }

    private static string NormalizeObjectJson(string? value)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The value must be a JSON object.", nameof(value));
        }

        return document.RootElement.GetRawText();
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture),
        };
    }
}
