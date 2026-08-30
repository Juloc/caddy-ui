using CaddyUi.Infrastructure.Certificates;

namespace CaddyUi.Infrastructure.Tests.Certificates;

public sealed class CertificateStatusServiceTests
{
    [Fact]
    public void AdditionalAppliedCertificateNamesKeepOnlyNestedNamesForManagedDomain()
    {
        IReadOnlySet<string> appliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "juloc.de",
            "*.juloc.de",
            "*.os.juloc.de",
            "os.juloc.de",
            "app.os.juloc.de",
            "*.other.example",
            "notjuloc.de",
        };

        var result = CertificateStatusService.GetAdditionalAppliedCertificateNames(
            "juloc.de",
            appliedNames);

        Assert.Equal(
            new[] { "*.os.juloc.de", "app.os.juloc.de", "os.juloc.de" },
            result);
    }

    [Fact]
    public void AdditionalAppliedCertificateNamesNormalizeAndDeduplicateNames()
    {
        IReadOnlySet<string> appliedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "*.OS.JULOC.DE.",
            "*.os.juloc.de",
            "API.OS.JULOC.DE.",
        };

        var result = CertificateStatusService.GetAdditionalAppliedCertificateNames(
            "JULOC.DE.",
            appliedNames);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, name => string.Equals(name, "*.os.juloc.de", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, name => string.Equals(name, "api.os.juloc.de", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdditionalAppliedCertificateNamesDoNotLeakSiblingDomains()
    {
        IReadOnlySet<string> appliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "*.os.juloc.de",
            "*.os.example.de",
            "juloc.de.evil.example",
        };

        var result = CertificateStatusService.GetAdditionalAppliedCertificateNames(
            "juloc.de",
            appliedNames);

        Assert.Equal(new[] { "*.os.juloc.de" }, result);
    }
}
