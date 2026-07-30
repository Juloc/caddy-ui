using System.Net.Mail;
using System.Text;
using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Certificates;

public sealed record AcmeEmailState(
    string Email,
    bool UsesEnvironmentVariable)
{
    public bool IsConfigured => Email.Length > 0 || UsesEnvironmentVariable;
}

public sealed record AcmeEmailUpdateResult(
    string Email,
    bool Changed);

public sealed class AcmeEmailService
{
    private const string ManagedMarkerName = ".caddy-ui-acme-email-managed";
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private readonly RoutingOptions _options;
    private readonly ICaddyCommandRunner _commandRunner;

    public AcmeEmailService(
        RoutingOptions options,
        ICaddyCommandRunner commandRunner)
    {
        _options = options;
        _commandRunner = commandRunner;
    }

    public async Task<AcmeEmailState> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.RootConfigPath))
        {
            return new AcmeEmailState(string.Empty, UsesEnvironmentVariable: false);
        }

        var content = await File.ReadAllTextAsync(_options.RootConfigPath, cancellationToken);
        return ReadState(content);
    }

    public async Task<AcmeEmailUpdateResult> UpdateAsync(
        string? email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            return await UpdateCoreAsync(normalizedEmail, cancellationToken);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private async Task<AcmeEmailUpdateResult> UpdateCoreAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var rootConfigPath = _options.RootConfigPath;
        if (!File.Exists(rootConfigPath))
        {
            throw new InvalidOperationException($"Das Caddyfile wurde nicht gefunden: {rootConfigPath}");
        }

        var directory = Path.GetDirectoryName(rootConfigPath) ??
            throw new InvalidOperationException("Der Caddyfile-Pfad hat kein gültiges Verzeichnis.");
        var markerPath = Path.Combine(directory, ManagedMarkerName);
        var originalContent = await File.ReadAllTextAsync(rootConfigPath, cancellationToken);
        var updatedContent = Render(originalContent, normalizedEmail);
        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
        {
            try
            {
                var ownershipChanged = await EnsureManagedMarkerAsync(markerPath, cancellationToken);
                return new AcmeEmailUpdateResult(normalizedEmail, ownershipChanged);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Die UI-Verwaltung der ACME-E-Mail konnte nicht dauerhaft aktiviert werden.",
                    exception);
            }
        }

        var candidatePath = TemporaryPath(rootConfigPath, "candidate");
        var rollbackPath = TemporaryPath(rootConfigPath, "rollback");
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
                "Die vollständige Caddy-Konfiguration ist mit dieser ACME-E-Mail ungültig.");

            File.Move(candidatePath, rootConfigPath, overwrite: true);
            replacedRootConfig = true;

            EnsureSucceeded(
                await RunCaddyAsync(
                    ["reload", "--config", rootConfigPath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Caddy konnte die neue ACME-E-Mail nicht laden.");
            EnsureSucceeded(
                await RunCaddyAsync(
                    ["validate", "--config", rootConfigPath, "--adapter", "caddyfile"],
                    cancellationToken),
                "Die aktive Caddy-Konfiguration ist nach dem Reload ungültig.");
            await EnsureManagedMarkerAsync(markerPath, cancellationToken);

            return new AcmeEmailUpdateResult(normalizedEmail, Changed: true);
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

    private static async Task<bool> EnsureManagedMarkerAsync(
        string markerPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(markerPath))
        {
            return false;
        }

        var temporaryPath = $"{markerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                "managed-by=caddy-ui\n",
                Utf8WithoutBom,
                cancellationToken);
            File.Move(temporaryPath, markerPath, overwrite: true);
            return true;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static AcmeEmailState ReadState(string content)
    {
        var lines = SplitLines(content);
        var block = FindGlobalBlock(lines);
        if (block is null || block.EmailLineIndexes.Count == 0)
        {
            return new AcmeEmailState(string.Empty, UsesEnvironmentVariable: false);
        }

        foreach (var index in block.EmailLineIndexes)
        {
            var value = EmailValue(lines[index]);
            if (string.Equals(value, "{$ACME_EMAIL}", StringComparison.Ordinal))
            {
                return new AcmeEmailState(string.Empty, UsesEnvironmentVariable: true);
            }

            if (value.Length > 0)
            {
                return new AcmeEmailState(Unquote(value), UsesEnvironmentVariable: false);
            }
        }

        return new AcmeEmailState(string.Empty, UsesEnvironmentVariable: false);
    }

    private static string Render(string content, string email)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(content).ToList();
        var block = FindGlobalBlock(lines);
        if (block is null)
        {
            if (email.Length == 0)
            {
                return content;
            }

            var prefix = string.Join(
                newline,
                "{",
                $"    email {email}",
                "}",
                string.Empty);
            return content.Length == 0 ? prefix : $"{prefix}{newline}{content}";
        }

        var emailIndexes = block.EmailLineIndexes.ToHashSet();
        var insertionIndex = block.EmailLineIndexes.Count > 0
            ? block.EmailLineIndexes[0]
            : block.CloseLineIndex;
        var result = new List<string>(lines.Count + 1);
        for (var index = 0; index < lines.Count; index++)
        {
            if (index == insertionIndex && email.Length > 0)
            {
                result.Add($"    email {email}");
            }

            if (!emailIndexes.Contains(index))
            {
                result.Add(lines[index]);
            }
        }

        return string.Join(newline, result);
    }

    private static GlobalOptionsBlock? FindGlobalBlock(IReadOnlyList<string> lines)
    {
        var openLineIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = ContentBeforeComment(lines[index]).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!string.Equals(trimmed, "{", StringComparison.Ordinal))
            {
                return null;
            }

            openLineIndex = index;
            break;
        }

        if (openLineIndex < 0)
        {
            return null;
        }

        var depth = 1;
        var emailLineIndexes = new List<int>();
        for (var index = openLineIndex + 1; index < lines.Count; index++)
        {
            var trimmed = ContentBeforeComment(lines[index]).Trim();
            if (depth == 1 && IsEmailDirective(trimmed))
            {
                emailLineIndexes.Add(index);
            }

            depth += StructuralBraceDelta(lines[index]);
            if (depth == 0)
            {
                return new GlobalOptionsBlock(index, emailLineIndexes);
            }
        }

        throw new InvalidOperationException("Der globale Optionsblock im Caddyfile ist nicht geschlossen.");
    }

    private static IReadOnlyList<string> SplitLines(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool IsEmailDirective(string value)
    {
        return string.Equals(value, "email", StringComparison.Ordinal) ||
               value.StartsWith("email ", StringComparison.Ordinal);
    }

    private static string EmailValue(string line)
    {
        var value = ContentBeforeComment(line).Trim();
        return value.Length <= "email".Length
            ? string.Empty
            : value["email".Length..].Trim();
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

    private static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var candidate = email.Trim();
        if (candidate.Length > 254 ||
            !MailAddress.TryCreate(candidate, out var address) ||
            !string.Equals(address.Address, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Die ACME-E-Mail-Adresse ist ungültig.", nameof(email));
        }

        return address.Address;
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
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

    private sealed record GlobalOptionsBlock(
        int CloseLineIndex,
        IReadOnlyList<int> EmailLineIndexes);
}
