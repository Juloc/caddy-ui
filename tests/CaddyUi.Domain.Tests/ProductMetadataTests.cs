using CaddyUi.Domain;

namespace CaddyUi.Domain.Tests;

public sealed class ProductMetadataTests
{
    [Fact]
    public void FoundationVersion_IdentifiesCurrentBeta()
    {
        Assert.Equal("2.0.0-beta.1", ProductMetadata.FoundationVersion);
    }
}
