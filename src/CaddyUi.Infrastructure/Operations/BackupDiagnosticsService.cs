using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CaddyUi.Infrastructure.Operations;

public sealed record GeneratedArtifact(string FileName, byte[] Content, string ContentType);

public sealed class BackupDiagnosticsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly OperationsStore _store;
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly OperationsOptions _options;
    private readonly RoutingOptions _routingOptions;
    private readonly string _connectionString;

    public BackupDiagnosticsService(
        OperationsStore store,
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        OperationsOptions options,
        RoutingOptions routingOptions,
        IConfiguration configuration)
    {
        _store = store;
        _contextFactory = contextFactory;
        _options = options;
        _routingOptions = routingOptions;
        _connectionString = configuration.GetConnectionString("CaddyUi") ?? DependencyInjection.DefaultConnectionString;
    }

    public async Task<ProviderOperationResult> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.BackupDirectory);
        var createdAt = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var fileName = $"caddy-ui-{createdAt:yyyyMMdd-HHmmss}.zip";
        var outputPath = Path.Combine(_options.BackupDirectory, fileName);
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"caddy-ui-backup-{id:N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var databasePath = Path.Combine(tempDirectory, "database.dump");
            await RunPgDumpAsync(databasePath, cancellationToken);
            var diagnostics = await CreateDiagnosticsAsync(cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(tempDirectory, "diagnostics.zip"), diagnostics.Content, cancellationToken);
            CopyIfExists(_routingOptions.RootConfigPath, tempDirectory, "Caddyfile");
            CopyIfExists(_routingOptions.ManagedFragmentPath, tempDirectory, "managed-routes.caddy");
            CopyIfExists(_routingOptions.ShadowFragmentPath, tempDirectory, "managed-routes.shadow.caddy");

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            ZipFile.CreateFromDirectory(tempDirectory, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var manifest = JsonSerializer.Serialize(new
            {
                schema = "caddy-ui-backup-v1",
                id,
                createdAt,
                fileName,
                digest,
                includes = new[] { "PostgreSQL custom dump", "redacted diagnostics", "available Caddy configuration files" },
            }, JsonOptions);
            var artifact = new BackupArtifactRecord(
                id,
                createdAt,
                fileName,
                outputPath,
                bytes.LongLength,
                digest,
                "ok",
                string.Empty,
                manifest);
            await _store.RecordBackupAsync(artifact, cancellationToken);
            CleanupOldBackups();
            return ProviderOperationResult.Success($"Backup {fileName} created ({bytes.LongLength:N0} bytes).", id.ToString("D"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var artifact = new BackupArtifactRecord(
                id,
                createdAt,
                fileName,
                outputPath,
                0,
                string.Empty,
                "failed",
                Limit(exception.Message, 4000),
                "{}");
            await _store.RecordBackupAsync(artifact, cancellationToken);
            return ProviderOperationResult.Failure(exception.Message);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public async Task<GeneratedArtifact> CreateDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var providers = new List<object>();
        var domains = new List<object>();
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT provider_type, label, enabled, config_json::text,
                           secret_references_json::text, last_tested_at,
                           last_test_status, last_test_error
                    FROM caddy_ui.dns_providers
                    ORDER BY lower(label)
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var config = OperationsJson.ReadStringObject(reader.GetString(3));
                    var secretKeys = OperationsJson.ReadStringObject(reader.GetString(4)).Keys.Order(StringComparer.Ordinal).ToArray();
                    providers.Add(new
                    {
                        type = reader.GetString(0),
                        label = reader.GetString(1),
                        enabled = reader.GetBoolean(2),
                        config,
                        secretKeys,
                        lastTestedAt = reader.IsDBNull(5) ? null : reader.GetValue(5),
                        status = reader.GetString(6),
                        error = reader.GetString(7),
                    });
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT name, display_name, enabled, is_default,
                           default_certificate_mode, dns_provider_id
                    FROM caddy_ui.managed_domains
                    ORDER BY is_default DESC, lower(name)
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    domains.Add(new
                    {
                        name = reader.GetString(0),
                        displayName = reader.GetString(1),
                        enabled = reader.GetBoolean(2),
                        isDefault = reader.GetBoolean(3),
                        certificateMode = reader.GetString(4),
                        providerId = reader.IsDBNull(5) ? null : reader.GetGuid(5).ToString("D"),
                    });
                }
            }
        }

        var jobs = await _store.ListJobsAsync(cancellationToken);
        var health = await _store.ListHealthTargetsAsync(cancellationToken);
        var ddns = await _store.ListDdnsTargetsAsync(cancellationToken);
        var payload = new
        {
            schema = "caddy-ui-diagnostics-v1",
            generatedAt = DateTimeOffset.UtcNow,
            runtime = new
            {
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            },
            options = new
            {
                dnsWriteMode = _options.DnsWriteMode.ToString().ToLowerInvariant(),
                _options.WorkerEnabled,
                routingWriteMode = _routingOptions.WriteMode.ToString().ToLowerInvariant(),
                installedCaddyDnsModules = _options.InstalledCaddyDnsModules.Order(StringComparer.OrdinalIgnoreCase),
            },
            providers,
            domains,
            jobs = jobs.Select(job => new { job.Name, job.JobType, job.Enabled, job.IntervalSeconds, job.NextRunAt, job.LastRunAt, job.LastStatus, job.LastError }),
            health = health.Select(target => new { target.Name, target.TargetType, target.Url, target.Enabled, target.LastCheckedAt, target.LastStatus, target.LastHttpStatus, target.LastDurationMilliseconds, target.LastError }),
            ddns = ddns.Select(target => new { target.Fqdn, target.RecordType, target.Enabled, target.IntervalSeconds, target.AddressSource, target.LastValue, target.LastRunAt, target.LastStatus, target.LastError }),
        };

        await using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
        }

        return new GeneratedArtifact(
            $"caddy-ui-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip",
            memory.ToArray(),
            "application/zip");
    }

    private async Task RunPgDumpAsync(string outputPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.PgDumpBinary))
        {
            throw new InvalidOperationException($"pg_dump was not found at '{_options.PgDumpBinary}'.");
        }

        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var host = builder.Host ?? throw new InvalidOperationException("The PostgreSQL host is not configured.");
        var username = builder.Username ?? throw new InvalidOperationException("The PostgreSQL username is not configured.");
        var database = builder.Database ?? throw new InvalidOperationException("The PostgreSQL database is not configured.");
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PgDumpBinary,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--format=custom");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add(database);
        if (!string.IsNullOrEmpty(builder.Password))
        {
            startInfo.Environment["PGPASSWORD"] = builder.Password;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("pg_dump could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"pg_dump failed with exit code {process.ExitCode}: {Limit(error, 2000)} {Limit(output, 500)}");
        }
    }

    private void CleanupOldBackups()
    {
        if (!Directory.Exists(_options.BackupDirectory))
        {
            return;
        }

        foreach (var file in new DirectoryInfo(_options.BackupDirectory)
                     .GetFiles("caddy-ui-*.zip")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(_options.BackupRetentionCount))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void CopyIfExists(string source, string targetDirectory, string fileName)
    {
        if (File.Exists(source))
        {
            File.Copy(source, Path.Combine(targetDirectory, fileName), overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
