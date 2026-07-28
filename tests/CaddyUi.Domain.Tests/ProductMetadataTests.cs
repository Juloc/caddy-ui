using CaddyUi.Domain;

namespace CaddyUi.Domain.Tests;

public sealed class ProductMetadataTests
{
    [Fact]
    public void FoundationVersion_IdentifiesCurrentPhase()
    {
        Assert.Equal("2.0.0-phase6", ProductMetadata.FoundationVersion);
    }
}
