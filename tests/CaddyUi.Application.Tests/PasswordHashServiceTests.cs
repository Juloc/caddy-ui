using CaddyUi.Application.Security;

namespace CaddyUi.Application.Tests;

public sealed class PasswordHashServiceTests
{
    private const string LegacyHash =
        "scrypt$16384$8$1$MDEyMzQ1Njc4OWFiY2RlZg$Vv7xre2SNvADowpBkoNxKSKptlwYLy5MGrDpqPpqhWg";

    [Fact]
    public void Pbkdf2Hash_RoundTrips()
    {
        var service = new PasswordHashService();
        var encoded = service.HashPassword("correct-horse-battery-staple");

        var valid = service.Verify("correct-horse-battery-staple", encoded);
        var invalid = service.Verify("wrong-password", encoded);

        Assert.True(valid.Succeeded);
        Assert.Null(valid.UpgradedHash);
        Assert.False(invalid.Succeeded);
    }

    [Fact]
    public void LegacyScrypt_IsVerifiedAndRehashed()
    {
        var service = new PasswordHashService();

        var result = service.Verify(
            "correct-horse-battery-staple",
            LegacyHash);

        Assert.True(result.Succeeded);
        Assert.StartsWith("pbkdf2-sha256$", result.UpgradedHash, StringComparison.Ordinal);
        Assert.True(service.Verify("correct-horse-battery-staple", result.UpgradedHash!).Succeeded);
    }
}
