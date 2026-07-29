using CaddyUi.Domain;

namespace CaddyUi.Domain.Tests;

public sealed class ProductMetadataTests
{
    [Fact]
    public void FoundationVersion_IdentifiesPhaseOne()
    {
        Assert.Equal("2.0.0-phase1", ProductMetadata.FoundationVersion);
    }
}
