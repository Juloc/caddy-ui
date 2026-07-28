using CaddyUi.Application.Security;

namespace CaddyUi.Application.Tests;

public sealed class TotpServiceTests
{
    [Fact]
    public void TotpCode_VerifiesInsideTheAllowedClockWindow()
    {
        var service = new TotpService();
        const string secret = "JBSWY3DPEHPK3PXP";
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = service.ComputeCode(secret, timestamp);

        Assert.True(service.VerifyCode(secret, code, timestamp));
        Assert.True(service.VerifyCode(secret, code, timestamp.AddSeconds(29)));
        Assert.False(service.VerifyCode(secret, "000000", timestamp));
    }

    [Fact]
    public void RecoveryCodes_AreUniqueAndHashable()
    {
        var service = new TotpService();
        var codes = service.GenerateRecoveryCodes();

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Equal(64, service.HashRecoveryCode(code).Length));
    }
}
