using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace CaddyUi.Infrastructure.Operations;

public interface ISecretReferenceResolver
{
    ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

public interface ISecretReferenceProtector
{
    string ProtectOrReference(string value);
}

public sealed partial class SecretReferenceResolver : ISecretReferenceResolver, ISecretReferenceProtector
{
    private const string ProtectedPrefix = "protected://v1/";
    private const string EnvironmentPrefix = "secret://env/";
    private const string FilePrefix = "secret://file/";
    private readonly IDataProtector _protector;

    public SecretReferenceResolver(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("CaddyUi.DnsProviderSecrets.v1");
    }

    public string ProtectOrReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith("env://", StringComparison.OrdinalIgnoreCase))
        {
            return EnvironmentPrefix + normalized[6..];
        }

        return ProtectedPrefix + _protector.Protect(normalized);
    }

    public async ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var normalized = reference.Trim();
        if (normalized.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            try
            {
                return _protector.Unprotect(normalized[ProtectedPrefix.Length..]);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "The encrypted DNS provider secret cannot be decrypted with the current data-protection keys.",
                    exception);
            }
        }

        if (EnvironmentVariableName().IsMatch(normalized))
        {
            return Environment.GetEnvironmentVariable(normalized) ??
                throw new InvalidOperationException($"The environment variable '{normalized}' is not configured.");
        }

        if (normalized.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = normalized[EnvironmentPrefix.Length..];
            if (!EnvironmentVariableName().IsMatch(name))
            {
                throw new InvalidOperationException("The environment secret reference is invalid.");
            }

            return Environment.GetEnvironmentVariable(name) ??
                throw new InvalidOperationException($"The environment variable '{name}' is not configured.");
        }

        if (normalized.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(normalized[FilePrefix.Length..]);
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException("File secret references must use an absolute path.");
            }

            var value = await File.ReadAllTextAsync(path, cancellationToken);
            return value.TrimEnd('\r', '\n');
        }

        throw new InvalidOperationException(
            "Unsupported secret reference. Enter the secret in the UI or use secret://env/NAME or secret://file/absolute/path.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();
}
