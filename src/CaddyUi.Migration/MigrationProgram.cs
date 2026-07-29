using CaddyUi.Infrastructure;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Migration;

public static class MigrationProgram
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var commandLine = MigrationCommandLine.Parse(args);
        if (commandLine.ShowHelp)
        {
            await Console.Out.WriteLineAsync(MigrationCommandLine.Usage);
            return 0;
        }

        if (commandLine.Command == "inspect" ||
            commandLine.Command == "import" && commandLine.DryRun)
        {
            var reader = new LegacySqliteReader();
            var report = commandLine.Command == "inspect"
                ? await CreateInspectionReportAsync(
                    reader,
                    commandLine,
                    cancellationToken)
                : LegacyImportService.CreatePlan(
                    await reader.InspectAsync(
                        commandLine.SourcePath!,
                        cancellationToken),
                    commandLine.IncludeRawRequests,
                    DateTimeOffset.UtcNow);
            await WriteReportAsync(report, commandLine.ReportPath, cancellationToken);
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
            options.UseUtcTimestamp = true;
        });

        builder.Services.AddCaddyUiInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<LegacySqliteReader>();
        builder.Services.AddScoped<LegacyImportService>();

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();

        if (commandLine.Command == "schema")
        {
            var database = scope.ServiceProvider.GetRequiredService<CaddyUiDbContext>();
            await database.Database.MigrateAsync(cancellationToken);
            var migrations = await database.Database.GetAppliedMigrationsAsync(
                cancellationToken);
            await Console.Out.WriteLineAsync(
                $"Applied migrations: {string.Join(", ", migrations)}");
            return 0;
        }

        var service = scope.ServiceProvider.GetRequiredService<LegacyImportService>();
        LegacyMigrationReport result;

        if (commandLine.Command == "verify")
        {
            result = await service.VerifyAsync(
                commandLine.SourcePath!,
                cancellationToken);
        }
        else
        {
            result = await service.ImportAsync(
                new LegacyMigrationOptions(
                    commandLine.SourcePath!,
                    commandLine.BackupDirectory,
                    commandLine.ReportPath,
                    DryRun: false,
                    commandLine.IncludeRawRequests),
                cancellationToken);
        }

        await WriteReportAsync(result, commandLine.ReportPath, cancellationToken);
        return result.Status is "succeeded" or "already-imported" or "verified"
            ? 0
            : 2;
    }

    private static async Task<LegacyMigrationReport> CreateInspectionReportAsync(
        LegacySqliteReader reader,
        MigrationCommandLine commandLine,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var inventory = await reader.InspectAsync(
            commandLine.SourcePath!,
            cancellationToken);

        var tables = inventory.Tables
            .Select(table => new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                0,
                table.RowCount,
                0,
                "inspection-only",
                $"Columns: {string.Join(", ", table.Columns)}"))
            .ToArray();

        return new LegacyMigrationReport
        {
            Mode = commandLine.Command,
            SourcePath = inventory.SourcePath,
            SourceDigest = inventory.SourceDigest,
            SourceSizeBytes = inventory.SourceSizeBytes,
            SourceSchemaVersion = inventory.SchemaVersion,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Status = "inspected",
            DryRun = true,
            AlreadyImported = false,
            Tables = tables,
            Warnings = Array.Empty<string>()
        };
    }

    private static async Task WriteReportAsync(
        LegacyMigrationReport report,
        string? reportPath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            await report.WriteAsync(reportPath, cancellationToken);
        }

        await Console.Out.WriteLineAsync(report.ToJson());
    }
}
