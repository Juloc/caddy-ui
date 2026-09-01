using CaddyUi.Web.Pages.Administration;

namespace CaddyUi.Web.Tests;

public sealed class DomainDdnsWizardTests
{
    [Fact]
    public void Defaults_CreateRootAndWildcardIpv4Targets()
    {
        var input = new DomainsModel.DomainInput();

        var targets = DomainsModel.BuildAutomaticDnsTargets(input).ToArray();

        Assert.Equal(2, targets.Length);
        Assert.Contains(targets, target => target.Name == "@" && target.RecordType == "A" && target.Enabled);
        Assert.Contains(targets, target => target.Name == "*" && target.RecordType == "A" && target.Enabled);
        Assert.DoesNotContain(targets, target => target.RecordType == "AAAA");
    }

    [Fact]
    public void Ipv6_AddsRootAndWildcardAaaaTargets()
    {
        var input = new DomainsModel.DomainInput
        {
            ConfigureIpv6 = true,
        };

        var targets = DomainsModel.BuildAutomaticDnsTargets(input).ToArray();

        Assert.Equal(4, targets.Length);
        Assert.Contains(targets, target => target.Name == "@" && target.RecordType == "A");
        Assert.Contains(targets, target => target.Name == "*" && target.RecordType == "A");
        Assert.Contains(targets, target => target.Name == "@" && target.RecordType == "AAAA");
        Assert.Contains(targets, target => target.Name == "*" && target.RecordType == "AAAA");
    }

    [Fact]
    public void DisabledContinuousUpdates_CreateOneShotTargets()
    {
        var input = new DomainsModel.DomainInput
        {
            KeepIpUpdated = false,
        };

        var targets = DomainsModel.BuildAutomaticDnsTargets(input).ToArray();

        Assert.All(targets, target => Assert.False(target.Enabled));
        Assert.All(targets, target => Assert.Equal("public", target.AddressSource));
    }
}
