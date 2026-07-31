using CaddyUi.Domain;

namespace CaddyUi.Domain.Tests;

public sealed class ProductMetadataTests
{
    [Fact]
    public void FoundationVersion_IdentifiesCurrentStableRelease()
    {
        Assert.Equal("2.1.3", ProductMetadata.FoundationVersion);
    }
}
