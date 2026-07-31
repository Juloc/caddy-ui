using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaddyUi.Infrastructure.Certificates;

namespace CaddyUi.Infrastructure.Tests.Certificates;

public sealed class CaddyCertificateStoreReaderTests
{
    [Fact]
    public void ReadSelectsNewestCurrentlyValidWildcardCertificate()
    {
        var directory = CreateDirectory();
        var now = DateTimeOffset.Parse(
            "2026-07-31T17:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var expiredPath = Path.Combine(
            directory,
            "acme-old",
            "wildcard_.juloc.de",
            "wildcard_.juloc.de.crt");
        var currentPath = Path.Combine(
            directory,
            "acme-current",
            "wildcard_.juloc.de",
            "wildcard_.juloc.de.crt");

        try
        {
            WriteCertificate(
                expiredPath,
                ["*.juloc.de"],
                now.AddMonths(-4),
                now.AddDays(-1));
            WriteCertificate(
                currentPath,
                ["*.juloc.de"],
                DateTimeOffset.Parse(
                    "2026-07-31T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(
                    "2026-10-29T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture));

            var snapshot = CaddyCertificateStoreReader.Read(directory);
            var certificate = snapshot.FindLatestValid("*.juloc.de", now);

            Assert.NotNull(certificate);
            Assert.Equal(currentPath, certificate.Path);
            Assert.Equal(
                DateTimeOffset.Parse(
                    "2026-07-31T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture),
                certificate.NotBefore);
            Assert.Equal(
                DateTimeOffset.Parse(
                    "2026-10-29T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture),
                certificate.ExpiresAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadKeepsBaseAndWildcardCertificatesSeparate()
    {
        var directory = CreateDirectory();
        var now = DateTimeOffset.Parse(
            "2026-07-31T17:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var wildcardPath = Path.Combine(
            directory,
            "acme",
            "wildcard_.juloc.de",
            "wildcard_.juloc.de.crt");
        var basePath = Path.Combine(
            directory,
            "acme",
            "juloc.de",
            "juloc.de.crt");

        try
        {
            WriteCertificate(
                wildcardPath,
                ["*.juloc.de", "juloc.de"],
                now.AddDays(-1),
                now.AddDays(120));
            WriteCertificate(
                basePath,
                ["juloc.de"],
                now.AddDays(-2),
                now.AddDays(90));

            var snapshot = CaddyCertificateStoreReader.Read(directory);
            var wildcard = snapshot.FindLatestValid("*.juloc.de", now);
            var baseDomain = snapshot.FindLatestValid("juloc.de", now);

            Assert.NotNull(wildcard);
            Assert.NotNull(baseDomain);
            Assert.Equal(wildcardPath, wildcard.Path);
            Assert.Equal(basePath, baseDomain.Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadDoesNotReuseAnOlderSnapshotAfterRenewal()
    {
        var directory = CreateDirectory();
        var now = DateTimeOffset.Parse(
            "2026-07-31T17:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var expiredPath = Path.Combine(
            directory,
            "acme-old",
            "wildcard_.juloc.de",
            "wildcard_.juloc.de.crt");
        var currentPath = Path.Combine(
            directory,
            "acme-current",
            "wildcard_.juloc.de",
            "wildcard_.juloc.de.crt");

        try
        {
            WriteCertificate(
                expiredPath,
                ["*.juloc.de"],
                now.AddMonths(-4),
                now.AddDays(-1));

            var beforeRenewal = CaddyCertificateStoreReader.Read(directory);
            Assert.Null(beforeRenewal.FindLatestValid("*.juloc.de", now));

            WriteCertificate(
                currentPath,
                ["*.juloc.de"],
                now.AddMinutes(-5),
                now.AddDays(90));

            var afterRenewal = CaddyCertificateStoreReader.Read(directory);
            var certificate = afterRenewal.FindLatestValid("*.juloc.de", now);

            Assert.NotNull(certificate);
            Assert.Equal(currentPath, certificate.Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadReportsMissingStoreAsUnreadable()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"caddy-ui-missing-certificate-store-{Guid.NewGuid():N}");

        var snapshot = CaddyCertificateStoreReader.Read(path);

        Assert.False(snapshot.DirectoryAvailable);
        Assert.False(snapshot.ReadSucceeded);
        Assert.Empty(snapshot.Artifacts);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"caddy-ui-certificate-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteCertificate(
        string path,
        IReadOnlyCollection<string> dnsNames,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsNames.First()}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            subjectAlternativeNames.AddDnsName(name);
        }

        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
    }
}
