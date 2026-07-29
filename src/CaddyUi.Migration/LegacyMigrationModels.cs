using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaddyUi.Migration;

public sealed record LegacyTableInventory(
    string Name,
    long RowCount,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> PrimaryKeyColumns);

public sealed record LegacyDatabaseInventory(
    string SourcePath,
    string SourceDigest,
    long SourceSizeBytes,
    int SchemaVersion,
    IReadOnlyList<LegacyTableInventory> Tables);

public sealed record LegacyTableMigrationResult(
    string TableName,
    long SourceRows,
    long ImportedRows,
    long PreservedRows,
    long SkippedRows,
    string TargetTable,
    string Note);

public sealed record LegacyMigrationOptions(
    string SourcePath,
    string BackupDirectory,
    string? ReportPath,
    bool DryRun,
    bool IncludeRawRequests);

public sealed record LegacyMigrationReport
{
    public required string Mode { get; init; }

    public required string SourcePath { get; init; }

    public required string SourceDigest { get; init; }

    public required long SourceSizeBytes { get; init; }

    public required int SourceSchemaVersion { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required string Status { get; init; }

    public required bool DryRun { get; init; }

    public required bool AlreadyImported { get; init; }

    public string? BackupPath { get; init; }

    public Guid? MigrationRunId { get; init; }

    public required IReadOnlyList<LegacyTableMigrationResult> Tables { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public string? Error { get; init; }

    [JsonIgnore]
    public long AccountedRows => Tables.Sum(
        table => table.ImportedRows + table.PreservedRows + table.SkippedRows);

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            fullPath,
            ToJson() + Environment.NewLine,
            cancellationToken);
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
