using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CaddyUi.Infrastructure.Certificates;

internal sealed record CaddyCertificateArtifact(
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    string Path,
    string StorageName,
    IReadOnlySet<string> DnsNames)
{
    public bool IsValidAt(DateTimeOffset timestamp) =>
        NotBefore <= timestamp && ExpiresAt > timestamp;
}

internal sealed record CaddyCertificateStoreSnapshot(
    bool DirectoryAvailable,
    bool ReadSucceeded,
    IReadOnlyList<CaddyCertificateArtifact> Artifacts)
{
    public CaddyCertificateArtifact? FindLatestValid(string name, DateTimeOffset now) =>
        FindCandidates(name)
            .Where(candidate => candidate.Artifact.IsValidAt(now))
            .OrderByDescending(candidate => candidate.Rank)
            .ThenByDescending(candidate => candidate.Artifact.NotBefore)
            .ThenByDescending(candidate => candidate.Artifact.ExpiresAt)
            .Select(candidate => candidate.Artifact)
            .FirstOrDefault();

    public CaddyCertificateArtifact? FindLatestHistorical(string name) =>
        FindCandidates(name)
            .OrderByDescending(candidate => candidate.Rank)
            .ThenByDescending(candidate => candidate.Artifact.ExpiresAt)
            .ThenByDescending(candidate => candidate.Artifact.NotBefore)
            .Select(candidate => candidate.Artifact)
            .FirstOrDefault();

    private IEnumerable<(CaddyCertificateArtifact Artifact, int Rank)> FindCandidates(string name)
    {
        var target = CaddyCertificateStoreReader.NormalizeName(name);
        var wildcardTarget = target.StartsWith("*.", StringComparison.Ordinal);
        foreach (var artifact in Artifacts)
        {
            var exactStorageMatch = string.Equals(
                artifact.StorageName,
                target,
                StringComparison.OrdinalIgnoreCase);
            var exactSanMatch = artifact.DnsNames.Contains(target);
            if (!exactStorageMatch && !exactSanMatch)
            {
                continue;
            }

            if (!wildcardTarget &&
                !exactStorageMatch &&
                (artifact.StorageName.StartsWith("*.", StringComparison.Ordinal) ||
                 artifact.DnsNames.Any(candidate => candidate.StartsWith("*.", StringComparison.Ordinal))))
            {
                continue;
            }

            yield return (artifact, exactStorageMatch ? 2 : 1);
        }
    }
}

internal static class CaddyCertificateStoreReader
{
    public static CaddyCertificateStoreSnapshot Read(string certificateDirectory)
    {
        if (string.IsNullOrWhiteSpace(certificateDirectory) ||
            !Directory.Exists(certificateDirectory))
        {
            return new CaddyCertificateStoreSnapshot(
                DirectoryAvailable: false,
                ReadSucceeded: false,
                Artifacts: []);
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(
                    certificateDirectory,
                    "*.crt",
                    SearchOption.AllDirectories)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CaddyCertificateStoreSnapshot(
                DirectoryAvailable: true,
                ReadSucceeded: false,
                Artifacts: []);
        }

        var artifacts = new List<CaddyCertificateArtifact>(files.Length);
        foreach (var file in files)
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                var names = ReadDnsNames(certificate);
                var storageName = ReadStorageName(file);
                if (storageName.Length == 0 && names.Count == 0)
                {
                    continue;
                }

                artifacts.Add(new CaddyCertificateArtifact(
                    new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                    new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                    file,
                    storageName,
                    names));
            }
            catch (Exception exception) when (
                exception is CryptographicException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return new CaddyCertificateStoreSnapshot(
            DirectoryAvailable: true,
            ReadSucceeded: true,
            Artifacts: artifacts);
    }

    internal static IReadOnlySet<string> ReadDnsNames(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddName(names, certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false));

        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null)
        {
            return names;
        }

        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);
            while (sequence.HasData)
            {
                if (sequence.PeekTag().HasSameClassAndValue(dnsTag))
                {
                    AddName(
                        names,
                        sequence.ReadCharacterString(UniversalTagNumber.IA5String, dnsTag));
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
        }

        return names;
    }

    internal static string NormalizeName(string? value)
    {
        var candidate = value?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        return candidate
            .Replace("wildcard_.", "*.", StringComparison.OrdinalIgnoreCase)
            .Replace("wildcard_", "*.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadStorageName(string file)
    {
        var fileName = NormalizeName(Path.GetFileNameWithoutExtension(file));
        if (LooksLikeDnsName(fileName))
        {
            return fileName;
        }

        var directoryName = NormalizeName(
            Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty));
        return LooksLikeDnsName(directoryName) ? directoryName : string.Empty;
    }

    private static void AddName(ISet<string> names, string? value)
    {
        var candidate = NormalizeName(value);
        if (LooksLikeDnsName(candidate))
        {
            names.Add(candidate);
        }
    }

    private static bool LooksLikeDnsName(string value) =>
        value.Length > 0 && value.Contains('.', StringComparison.Ordinal);
}
