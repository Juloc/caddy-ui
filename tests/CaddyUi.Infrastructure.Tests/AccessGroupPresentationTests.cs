using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AccessGroupPresentationTests
{
    [Fact]
    public void Create_NormalizesValidOptionalPresentation()
    {
        var presentation = AccessGroupPresentation.Create(
            "#0f6cbd",
            "https://example.test/icon.png");

        Assert.Equal("#0F6CBD", presentation.AccentColor);
        Assert.Equal(
            "https://example.test/icon.png",
            presentation.IconUrl);
        Assert.Equal("#0F6CBD", presentation.EffectiveAccentColor);
    }

    [Fact]
    public void FromJson_UsesSafeDefaultsForInvalidLegacyConfiguration()
    {
        var presentation = AccessGroupPresentation.FromJson(
            """{"accentColor":"red","iconUrl":"javascript:alert(1)"}""");

        Assert.Empty(presentation.AccentColor);
        Assert.Empty(presentation.IconUrl);
        Assert.Equal(
            AccessGroupPresentation.DefaultAccentColor,
            presentation.EffectiveAccentColor);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#123")]
    [InlineData("#12345678")]
    public void Create_RejectsInvalidAccentColor(string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => AccessGroupPresentation.Create(value, null));
    }

    [Theory]
    [InlineData("http://example.test/icon.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/icon.png")]
    public void Create_RejectsNonHttpsIconUrl(string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => AccessGroupPresentation.Create(null, value));
    }
}
