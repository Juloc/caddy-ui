using System.Text.RegularExpressions;

namespace CaddyUi.Infrastructure.Operations;

public interface ISecretReferenceResolver
{
    ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

public sealed partial class SecretReferenceResolver : ISecretReferenceResolver
{
    public async ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var normalized = reference.Trim();
        if (EnvironmentVariableName().IsMatch(normalized))
        {
            return Environment.GetEnvironmentVariable(normalized) ??
                throw new InvalidOperationException($"The environment variable '{normalized}' is not configured.");
        }

        if (normalized.StartsWith("secret://env/", StringComparison.OrdinalIgnoreCase))
        {
            var name = normalized[13..];
            if (!EnvironmentVariableName().IsMatch(name))
            {
                throw new InvalidOperationException("The environment secret reference is invalid.");
            }

            return Environment.GetEnvironmentVariable(name) ??
                throw new InvalidOperationException($"The environment variable '{name}' is not configured.");
        }

        if (normalized.StartsWith("secret://file/", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(normalized[14..]);
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException("File secret references must use an absolute path.");
            }

            var value = await File.ReadAllTextAsync(path, cancellationToken);
            return value.TrimEnd('\r', '\n');
        }

        throw new InvalidOperationException("Unsupported secret reference. Use an environment variable, secret://env/NAME or secret://file/absolute/path.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();
}
