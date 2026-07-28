using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CaddyUi.Application.Routing;

namespace CaddyUi.Infrastructure.Routing;

public sealed record CaddyCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public interface ICaddyCommandRunner
{
    Task<CaddyCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessCaddyCommandRunner : ICaddyCommandRunner
{
    private readonly RoutingOptions _options;

    public ProcessCaddyCommandRunner(RoutingOptions options)
    {
        _options = options;
    }

    public async Task<CaddyCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.CaddyBinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new CaddyCommandResult(-1, string.Empty, "Caddy could not be started.", false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CaddyCommandResult(-1, string.Empty, exception.Message, false);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return new CaddyCommandResult(
                process.ExitCode,
                Limit(await outputTask, 64_000),
                Limit(await errorTask, 64_000),
                false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new CaddyCommandResult(
                -1,
                Limit(await SafeReadAsync(outputTask), 64_000),
                Limit(await SafeReadAsync(errorTask), 64_000),
                true);
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string Limit(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }
}

public sealed record RoutePreviewResult(
    RouteRevisionRecord Revision,
    string CurrentContent,
    IReadOnlyList<DiffLine> Diff,
    IReadOnlyList<string> Warnings,
    RouteWriteMode WriteMode,
    string TargetPath);

public sealed record RouteApplyResult(
    Guid OperationId,
    string State,
    string Message);

public sealed class CaddyApplyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim ApplyLock = new(1, 1);
    private readonly RouteManagementStore _store;
    private readonly RoutingOptions _options;
    private readonly ICaddyCommandRunner _commandRunner;

    public CaddyApplyService(
        RouteManagementStore store,
        RoutingOptions options,
        ICaddyCommandRunner commandRunner)
    {
        _store = store;
        _options = options;
        _commandRunner = commandRunner;
    }

    public RoutingOptions Options => _options;

    public async Task<RoutePreviewResult> CreatePreviewAsync(
        string reason,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        var sources = await _store.LoadCompilerSourcesAsync(cancellationToken);
        var compiler = new CaddyRouteCompiler(_options.AllowCustomRoutes, _options.PortalUpstream);
        var compilation = compiler.Compile(sources);
        var current = await ReadCurrentContentAsync(cancellationToken);
        var revision = await _store.CreateRevisionAsync(compilation, reason, actor, cancellationToken);
        return new RoutePreviewResult(
            revision,
            current,
            LineDiff.Create(current, compilation.Content),
            compilation.Warnings,
            _options.WriteMode,
            TargetPath());
    }

    public async Task<RouteApplyResult> ApplyAsync(
        Guid revisionId,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        await ApplyLock.WaitAsync(cancellationToken);
        try
        {
            return await ApplyCoreAsync(revisionId, actor, cancellationToken);
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    public async Task<RouteApplyResult> RollbackLastAsync(
        string reason,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        await ApplyLock.WaitAsync(cancellationToken);
        try
        {
            return await RollbackCoreAsync(reason, actor, cancellationToken);
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    public async Task<string> ReadCurrentContentAsync(CancellationToken cancellationToken = default)
    {
        return await ReadFileIfExistsAsync(TargetPath(), cancellationToken);
    }

    private async Task<RouteApplyResult> ApplyCoreAsync(
        Guid revisionId,
        ManagementActor actor,
        CancellationToken cancellationToken)
    {
        if (_options.WriteMode == RouteWriteMode.Disabled)
        {
            throw new InvalidOperationException(
                "Route writes are disabled. Set Routing:WriteMode to shadow or active after the deployment paths have been verified.");
        }

        var revision = await _store.GetRevisionAsync(revisionId, cancellationToken) ??
            throw new InvalidOperationException("The selected route revision does not exist.");
        if (_options.WriteMode == RouteWriteMode.Active && RequiresWildcardRenderer(revision.ManifestJson))
        {
            throw new InvalidOperationException(
                "Der produktive Apply wurde blockiert: Die Revision enthält Wildcard- oder geerbte Zertifikate. Der DNS-/Wildcard-Renderer folgt in Phase 8. Shadow-Validierung bleibt verfügbar.");
        }

        var targetPath = TargetPath();
        var previousTargetExisted = File.Exists(targetPath);
        var previousContent = await ReadFileIfExistsAsync(targetPath, cancellationToken);
        Guid? snapshotId = null;
        if (_options.WriteMode == RouteWriteMode.Active)
        {
            VerifyActiveContract();
            snapshotId = await _store.CreateSnapshotAsync(
                previousContent,
                $"Before applying route revision {revision.Id:D}",
                cancellationToken);
        }

        var operationId = await _store.StartOperationAsync(
            revision.Id,
            actor,
            Guid.NewGuid().ToString("N"),
            snapshotId,
            cancellationToken);
        var sequence = 1;
        var replacedTarget = false;
        string? candidatePath = null;
        try
        {
            candidatePath = await WriteCandidateAsync(revision.Content, cancellationToken);
            await StepAsync(operationId, sequence++, "Generate candidate", new { candidatePath }, cancellationToken);

            var candidateValidation = await RunCaddyAsync(
                ["validate", "--config", candidatePath, "--adapter", "caddyfile"],
                cancellationToken);
            EnsureSucceeded(candidateValidation, "Caddy rejected the generated route fragment.");
            await StepAsync(
                operationId,
                sequence++,
                "Validate candidate",
                CommandDetails(candidateValidation),
                cancellationToken);

            await WriteAtomicallyAsync(targetPath, revision.Content, cancellationToken);
            replacedTarget = true;
            await StepAsync(
                operationId,
                sequence++,
                "Write managed fragment",
                new { targetPath },
                cancellationToken);

            if (_options.WriteMode == RouteWriteMode.Shadow)
            {
                await _store.CompleteOperationAsync(
                    operationId,
                    revision.Id,
                    "shadowed",
                    string.Empty,
                    actor,
                    cancellationToken);
                return new RouteApplyResult(
                    operationId,
                    "shadowed",
                    $"The validated candidate was written to the shadow path {targetPath}.");
            }

            await RunRequiredStepAsync(
                operationId,
                sequence++,
                "Validate complete configuration",
                ["validate", "--config", _options.RootConfigPath, "--adapter", "caddyfile"],
                "The complete Caddy configuration is invalid after inserting the managed fragment.",
                cancellationToken);
            await RunRequiredStepAsync(
                operationId,
                sequence++,
                "Reload Caddy",
                ["reload", "--config", _options.RootConfigPath, "--adapter", "caddyfile"],
                "Caddy reload failed.",
                cancellationToken);
            await RunRequiredStepAsync(
                operationId,
                sequence,
                "Verify active configuration",
                ["validate", "--config", _options.RootConfigPath, "--adapter", "caddyfile"],
                "Post-reload validation failed.",
                cancellationToken);

            await _store.CompleteOperationAsync(
                operationId,
                revision.Id,
                "applied",
                string.Empty,
                actor,
                cancellationToken);
            return new RouteApplyResult(
                operationId,
                "applied",
                "The route revision was validated, reloaded and verified.");
        }
        catch (Exception exception)
        {
            var rollbackError = string.Empty;
            if (_options.WriteMode == RouteWriteMode.Active && replacedTarget)
            {
                rollbackError = await TryRollbackFilesAsync(
                    targetPath,
                    previousContent,
                    previousTargetExisted,
                    operationId,
                    sequence,
                    cancellationToken);
            }

            var error = rollbackError.Length == 0
                ? exception.Message
                : $"{exception.Message} Rollback issue: {rollbackError}";
            await _store.CompleteOperationAsync(
                operationId,
                revision.Id,
                "failed",
                error,
                actor,
                cancellationToken);
            throw new InvalidOperationException(error, exception);
        }
        finally
        {
            if (candidatePath is not null)
            {
                TryDelete(candidatePath);
            }
        }
    }

    private async Task<RouteApplyResult> RollbackCoreAsync(
        string reason,
        ManagementActor actor,
        CancellationToken cancellationToken)
    {
        if (_options.WriteMode != RouteWriteMode.Active)
        {
            throw new InvalidOperationException("Rollback is available only in active route-write mode.");
        }

        VerifyActiveContract();
        var previousOperation = await _store.GetLatestAppliedOperationAsync(cancellationToken) ??
            throw new InvalidOperationException("No applied route operation with a previous snapshot exists.");
        var snapshot = await _store.GetSnapshotAsync(
            previousOperation.PreviousSnapshotId!.Value,
            cancellationToken) ?? throw new InvalidOperationException("The rollback snapshot is missing.");
        var targetPath = TargetPath();
        var current = await ReadFileIfExistsAsync(targetPath, cancellationToken);
        var currentSnapshot = await _store.CreateSnapshotAsync(
            current,
            $"Before rollback: {reason}",
            cancellationToken);
        var operationId = await _store.StartOperationAsync(
            null,
            actor,
            Guid.NewGuid().ToString("N"),
            currentSnapshot,
            cancellationToken);
        try
        {
            await WriteAtomicallyAsync(targetPath, snapshot.Content, cancellationToken);
            await StepAsync(
                operationId,
                1,
                "Restore snapshot",
                new { snapshot.Id, targetPath },
                cancellationToken);
            await RunRequiredStepAsync(
                operationId,
                2,
                "Validate rollback",
                ["validate", "--config", _options.RootConfigPath, "--adapter", "caddyfile"],
                "The restored snapshot does not produce a valid Caddy configuration.",
                cancellationToken);
            await RunRequiredStepAsync(
                operationId,
                3,
                "Reload rollback",
                ["reload", "--config", _options.RootConfigPath, "--adapter", "caddyfile"],
                "Caddy reload failed during rollback.",
                cancellationToken);
            await _store.CompleteOperationAsync(
                operationId,
                null,
                "rolled_back",
                string.Empty,
                actor,
                cancellationToken);
            return new RouteApplyResult(
                operationId,
                "rolled_back",
                "The previous managed-route snapshot is active again.");
        }
        catch (Exception exception)
        {
            await WriteAtomicallyAsync(targetPath, current, cancellationToken);
            await _store.CompleteOperationAsync(
                operationId,
                null,
                "failed",
                exception.Message,
                actor,
                cancellationToken);
            throw;
        }
    }

    private async Task RunRequiredStepAsync(
        Guid operationId,
        int sequence,
        string name,
        IReadOnlyList<string> arguments,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await RunCaddyAsync(arguments, cancellationToken);
        EnsureSucceeded(result, failureMessage);
        await StepAsync(operationId, sequence, name, CommandDetails(result), cancellationToken);
    }

    private async Task StepAsync(
        Guid operationId,
        int sequence,
        string name,
        object details,
        CancellationToken cancellationToken)
    {
        await _store.RecordOperationStepAsync(
            operationId,
            sequence,
            name,
            "success",
            JsonSerializer.Serialize(details, JsonOptions),
            string.Empty,
            cancellationToken);
    }

    private void VerifyActiveContract()
    {
        if (!Path.IsPathFullyQualified(_options.ManagedFragmentPath) ||
            !Path.IsPathFullyQualified(_options.RootConfigPath))
        {
            throw new InvalidOperationException("Active route paths must be absolute.");
        }

        if (!File.Exists(_options.RootConfigPath))
        {
            throw new InvalidOperationException($"The root Caddyfile does not exist: {_options.RootConfigPath}");
        }

        var root = File.ReadAllText(_options.RootConfigPath);
        var normalizedManaged = _options.ManagedFragmentPath.Replace('\\', '/');
        var normalizedRoot = root.Replace('\\', '/');
        var fileName = Path.GetFileName(normalizedManaged);
        if (!normalizedRoot.Contains(normalizedManaged, StringComparison.Ordinal) &&
            !normalizedRoot.Contains($"import {fileName}", StringComparison.Ordinal) &&
            !normalizedRoot.Contains($"import \"{fileName}\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The root Caddyfile does not import the configured managed-route fragment. Active apply was blocked.");
        }
    }

    private async Task<string> TryRollbackFilesAsync(
        string targetPath,
        string previousContent,
        bool previousTargetExisted,
        Guid operationId,
        int sequence,
        CancellationToken cancellationToken)
    {
        try
        {
            if (previousTargetExisted)
            {
                await WriteAtomicallyAsync(targetPath, previousContent, cancellationToken);
            }
            else
            {
                TryDelete(targetPath);
            }

            var reload = await RunCaddyAsync(
                ["reload", "--config", _options.RootConfigPath, "--adapter", "caddyfile"],
                cancellationToken);
            await _store.RecordOperationStepAsync(
                operationId,
                sequence,
                "Automatic rollback",
                reload.Succeeded ? "success" : "failed",
                JsonSerializer.Serialize(CommandDetails(reload), JsonOptions),
                reload.Succeeded ? string.Empty : ErrorText(reload),
                cancellationToken);
            return reload.Succeeded ? string.Empty : ErrorText(reload);
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private async Task<CaddyCommandResult> RunCaddyAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return await _commandRunner.RunAsync(
            arguments,
            TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
            cancellationToken);
    }

    private static object CommandDetails(CaddyCommandResult result)
    {
        return new
        {
            result.ExitCode,
            result.TimedOut,
            standardOutput = result.StandardOutput,
            standardError = result.StandardError,
        };
    }

    private static void EnsureSucceeded(CaddyCommandResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{message} {ErrorText(result)}".Trim());
        }
    }

    private static string ErrorText(CaddyCommandResult result)
    {
        if (result.TimedOut)
        {
            return "The Caddy command timed out.";
        }

        return string.IsNullOrWhiteSpace(result.StandardError)
            ? $"Exit code {result.ExitCode}."
            : result.StandardError.Trim();
    }

    private static bool RequiresWildcardRenderer(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            return document.RootElement.TryGetProperty(
                       "requiresWildcardCertificateRenderer",
                       out var property) &&
                   property.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private async Task<string> WriteCandidateAsync(
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(TargetPath()) ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $".caddy-ui-candidate-{Guid.NewGuid():N}.caddy");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("The target path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task<string> ReadFileIfExistsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : string.Empty;
    }

    private string TargetPath()
    {
        return _options.WriteMode == RouteWriteMode.Active
            ? _options.ManagedFragmentPath
            : _options.ShadowFragmentPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
