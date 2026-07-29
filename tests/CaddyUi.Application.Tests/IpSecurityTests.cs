using System.Net;
using CaddyUi.Application.Security;
using CaddyUi.Domain.Security;

namespace CaddyUi.Application.Tests;

public sealed class IpSecurityTests
{
    private readonly IpAddressClassifier _classifier = new();

    [Theory]
    [InlineData("10.1.2.3", IpAddressScope.Private)]
    [InlineData("192.168.1.1", IpAddressScope.Private)]
    [InlineData("127.0.0.1", IpAddressScope.Loopback)]
    [InlineData("169.254.10.20", IpAddressScope.LinkLocal)]
    [InlineData("100.64.0.1", IpAddressScope.Shared)]
    [InlineData("192.0.2.5", IpAddressScope.Documentation)]
    [InlineData("198.18.0.1", IpAddressScope.Benchmark)]
    [InlineData("2001:db8::1", IpAddressScope.Documentation)]
    [InlineData("fc00::1", IpAddressScope.Private)]
    [InlineData("fe80::1", IpAddressScope.LinkLocal)]
    public void NonPublicAddresses_DoNotAllowExternalLookup(
        string value,
        IpAddressScope expectedScope)
    {
        var result = _classifier.Classify(value);

        Assert.Equal(expectedScope, result.Scope);
        Assert.False(result.ExternalLookupAllowed);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddresses_AllowExternalLookup(string value)
    {
        var result = _classifier.Classify(value);

        Assert.Equal(IpAddressScope.Public, result.Scope);
        Assert.True(result.ExternalLookupAllowed);
    }

    [Fact]
    public void IPv4MappedAddress_IsNormalizedToIPv4()
    {
        var result = _classifier.Classify(IPAddress.Parse("::ffff:192.168.1.10"));

        Assert.Equal("192.168.1.10", result.NormalizedAddress);
        Assert.Equal(IpAddressScope.Private, result.Scope);
    }

    [Fact]
    public void NetworkParser_NormalizesHostAddressAndPrefix()
    {
        var result = IpNetworkParser.Parse("192.168.1.27/24");

        Assert.Equal("192.168.1.0/24", result.Cidr);
        Assert.False(result.IsSingleAddress);
    }

    [Fact]
    public void RiskEngine_IsDeterministicAndExplainsScore()
    {
        var engine = new ClientRiskEngine();
        var sample = new ClientRiskSample(
            "unknown",
            "ExampleScannerBot/1.0",
            900,
            TimeSpan.FromMinutes(1),
            0.98,
            140,
            12,
            0.72,
            0.31,
            4,
            10,
            true,
            DateTimeOffset.Parse("2026-07-28T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-28T10:01:00Z"));

        var first = engine.Assess(sample);
        var second = engine.Assess(sample);

        Assert.Equal(first.Classification, second.Classification);
        Assert.Equal(first.AutomationScore, second.AutomationScore);
        Assert.Equal(first.RiskLevel, second.RiskLevel);
        Assert.Equal(first.EngineVersion, second.EngineVersion);
        Assert.Equal(
            first.Reasons.Select(reason => (reason.Code, reason.Weight)),
            second.Reasons.Select(reason => (reason.Code, reason.Weight)));
        Assert.Equal(100, first.AutomationScore);
        Assert.Equal(ClientRiskLevel.High, first.RiskLevel);
        Assert.Equal("bot", first.Classification);
        Assert.Contains(first.Reasons, reason => reason.Code == "scanner-paths");
        Assert.Equal(ClientRiskEngine.CurrentVersion, first.EngineVersion);
    }

    [Fact]
    public void BenignBrowserSample_RemainsLowRisk()
    {
        var result = new ClientRiskEngine().Assess(
            new ClientRiskSample(
                "human",
                "Mozilla/5.0 Chrome/140.0",
                20,
                TimeSpan.FromMinutes(10),
                0.1,
                5,
                0,
                0,
                0,
                0,
                1,
                false,
                DateTimeOffset.Parse("2026-07-28T10:00:00Z"),
                DateTimeOffset.Parse("2026-07-28T10:10:00Z")));

        Assert.Equal(0, result.AutomationScore);
        Assert.Equal(ClientRiskLevel.Low, result.RiskLevel);
        Assert.Equal("human", result.Classification);
    }
}
