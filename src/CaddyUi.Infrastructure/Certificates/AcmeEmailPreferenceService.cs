using System.Text;
using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Certificates;

public sealed record AcmeEmailPreferenceUpdateResult(
    string Email,
    bool Changed,
    bool UsesEnvironmentVariable);

public sealed class AcmeEmailPreferenceService
{
    private const string EnvironmentVariableName = "ACME_EMAIL";
    private const string EnvironmentReference = "{$ACME_EMAIL}";
    private const string ManagedMarkerName = ".caddy-ui-acme-email-managed";
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private readonly AcmeEmailService _emailService;
    private readonly RoutingOptions _options;
    private readonly ICaddyCommandRunner _commandRunner;
    private readonly Func<string?> _environmentEmailProvider;

    public AcmeEmailPreferenceService(
        AcmeEmailService emailService,
        RoutingOptions options,
        ICaddyCommandRunner commandRunner)
        : this(
            emailService,
            options,
            commandRunner,
            static () => Environment.GetEnvironmentVariable(EnvironmentVariableName))
    {
    }

    public AcmeEmailPreferenceService(
        AcmeEmailService emailService,
        RoutingOptions options,
        ICaddyCommandRunner commandRunner,
        Func<string?> environmentEmailProvider)
    {
        _emailService = emailService;
        _options = options;
        _commandRunner = commandRunner;
        _environmentEmailProvider = environmentEmailProvider;
    }

    public Task<AcmeEmailState> ReadAsync(CancellationToken cancellationToken = default)
    {
        return _emailService.ReadAsync(cancellationToken);
    }

    public async Task<AcmeEmailPreferenceUpdateResult> UpdateAsync(
        string? email,
        CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                var configured = await _emailService.UpdateAsync(email, cancellationToken);
                return new AcmeEmailPreferenceUpdateResult(
                    configured.Email,
                    configured.Changed,
                    UsesEnvironmentVariable: false);
            }

            return await UseEnvironmentFallbackAsync(cancellationToken);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private async Task<AcmeEmailPreferenceUpdateResult> UseEnvironmentFallbackAsync(
        CancellationToken cancellationToken)
    {
        var environmentEmail = _environmentEmailProvider()?.Trim() ?? string.Empty;
        var state = await _emailService.ReadAsync(cancellationToken);
        if (environmentEmail.Length == 0)
        {
            var changed = false;
            if (state.IsConfigured)
            {
                var removed = await _emailService.UpdateAsync(string.Empty, cancellationToken);
                changed = removed.Changed;
            }

            changed |= DeleteManagedMarker();
            return new AcmeEmailPreferenceUpdateResult(
                string.Empty,
                changed,
                UsesEnvironmentVariable: false);
        }

        if (state.UsesEnvironmentVariable)
        {
            var markerRemoved = DeleteManagedMarker();
            return new AcmeEmailPreferenceUpdateResult(
                string.Empty,
                markerRemoved,
                UsesEnvironmentVariable: true);
        }

        var configured = await _emailService.UpdateAsync(environmentEmail, cancellationToken);
        var replaced = await ReplaceLiteralWithEnvironmentReferenceAsync(
            configured.Email,
            cancellationToken);
        var ownershipChanged = DeleteManagedMarker();

        return new AcmeEmailPreferenceUpdateResult(
            string.Empty,
            configured.Changed || replaced || ownershipChanged,
            UsesEnvironmentVariable: true);
    }

    private async Task<bool> ReplaceLiteralWithEnvironmentReferenceAsync(
        string literalEmail,
        CancellationToken cancellationToken)
    {
        var rootConfigPath = _options.RootConfigPath;
        if (!File.Exists(rootConfigPath))
        {
            throw new InvalidOperationException($"Das Caddyfile wurde nicht gefunden: {rootConfigPath}");
        }

        var originalContent = await File.ReadAllTextAsync(rootConfigPath, cancellationToken);
        var updatedContent = RenderEnvironmentReference(originalContent, literalEmail);
        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
        {
            return false;
        }

        var candidatePath = TemporaryPath(rootConfigPath, "environment-candidate");
        var rollbackPath = TemporaryPath(rootConfigPath, "environment-rollback");
        var replacedRootConfig = false;
        try
        {
            await File.WriteAllTextAsync(
                candidatePath,
                updatedContent,
                Utf8WithoutBom,
                cancellationToken);
            EnsureSucceeded(
                await RunCaddyAsync(
                    ["validate", "--config", candidatePath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Die vollständige Caddy-Konfiguration ist mit ACME_EMAIL ungültig.");

            File.Move(candidatePath, rootConfigPath, overwrite: true);
            replacedRootConfig = true;

            EnsureSucceeded(
                await RunCaddyAsync(
                    ["reload", "--config", rootConfigPath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Caddy konnte ACME_EMAIL nicht laden.");
            EnsureSucceeded(
                await RunCaddyAsync(
                    ["validate", "--config", rootConfigPath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Die aktive Caddy-Konfiguration ist nach dem Reload ungültig.");

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var rollbackError = string.Empty;
            if (replacedRootConfig)
            {
                rollbackError = await TryRollbackAsync(
                    rootConfigPath,
                    rollbackPath,
                    originalContent,
                    cancellationToken);
            }

            var message = rollbackError.Length == 0
                ? exception.Message
                : $"{exception.Message} Rollback-Fehler: {rollbackError}";
            throw new InvalidOperationException(message, exception);
        }
        finally
        {
            TryDelete(candidatePath);
            TryDelete(rollbackPath);
        }
    }

    private async Task<string> TryRollbackAsync(
        string rootConfigPath,
        string rollbackPath,
        string originalContent,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(
                rollbackPath,
                originalContent,
                Utf8WithoutBom,
                cancellationToken);
            File.Move(rollbackPath, rootConfigPath, overwrite: true);
            EnsureSucceeded(
                await RunCaddyAsync(
                    ["reload", "--config", rootConfigPath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Caddy konnte die vorherige Konfiguration nicht wieder laden.");
            EnsureSucceeded(
                await RunCaddyAsync(
                    ["validate", "--config", rootConfigPath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Die wiederhergestellte Caddy-Konfiguration ist ungültig.");
            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception.Message;
        }
    }

    private Task<CaddyCommandResult> RunCaddyAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return _commandRunner.RunAsync(
            arguments,
            TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
            cancellationToken);
    }

    private bool DeleteManagedMarker()
    {
        var directory = Path.GetDirectoryName(_options.RootConfigPath) ??
            throw new InvalidOperationException("Der Caddyfile-Pfad hat kein gültiges Verzeichnis.");
        var markerPath = Path.Combine(directory, ManagedMarkerName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            File.Delete(markerPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Die ACME_EMAIL-Fallback-Konfiguration konnte nicht dauerhaft aktiviert werden.",
                exception);
        }
    }

    private static string RenderEnvironmentReference(string content, string literalEmail)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(content).ToList();
        var openLineIndex = FindGlobalBlockStart(lines);
        var depth = 1;
        var expectedDirective = $"email {literalEmail}";
        for (var index = openLineIndex + 1; index < lines.Count; index++)
        {
            var trimmed = ContentBeforeComment(lines[index]).Trim();
            if (depth == 1 && string.Equals(trimmed, expectedDirective, StringComparison.Ordinal))
            {
                var indentationLength = lines[index].Length - lines[index].TrimStart().Length;
                var indentation = lines[index][..indentationLength];
                lines[index] = $"{indentation}email {EnvironmentReference}";
                return string.Join(newline, lines);
            }

            depth += StructuralBraceDelta(lines[index]);
            if (depth == 0)
            {
                break;
            }
        }

        throw new InvalidOperationException(
            "Die zuvor gesetzte ACME-E-Mail wurde im globalen Caddy-Optionsblock nicht gefunden.");
    }

    private static int FindGlobalBlockStart(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = ContentBeforeComment(lines[index]).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (string.Equals(trimmed, "{", StringComparison.Ordinal))
            {
                return index;
            }

            break;
        }

        throw new InvalidOperationException("Das Caddyfile enthält keinen globalen Optionsblock.");
    }

    private static IReadOnlyList<string> SplitLines(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string ContentBeforeComment(string line)
    {
        var inQuotes = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\' && inQuotes)
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (character == '#' && !inQuotes)
            {
                return line[..index];
            }
        }

        return line;
    }

    private static int StructuralBraceDelta(string line)
    {
        var content = ContentBeforeComment(line);
        var inQuotes = false;
        var escaped = false;
        var delta = 0;
        foreach (var character in content)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\' && inQuotes)
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (character == '{')
            {
                delta++;
            }
            else if (character == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static string TemporaryPath(string rootConfigPath, string purpose)
    {
        var directory = Path.GetDirectoryName(rootConfigPath) ??
            throw new InvalidOperationException("Der Caddyfile-Pfad hat kein gültiges Verzeichnis.");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(rootConfigPath)}.acme-{purpose}-{Guid.NewGuid():N}.tmp");
    }

    private static void EnsureSucceeded(CaddyCommandResult result, string message)
    {
        if (result.Succeeded)
        {
            return;
        }

        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        if (result.TimedOut)
        {
            details = details.Length == 0 ? "Zeitüberschreitung." : $"Zeitüberschreitung. {details}";
        }

        throw new InvalidOperationException(details.Length == 0 ? message : $"{message} {details}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
