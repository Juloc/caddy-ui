using CaddyUi.Contracts;
using CaddyUi.Domain;

namespace CaddyUi.Application;

public sealed class FoundationStatusService
{
    public FoundationStatus GetStatus()
    {
        return new FoundationStatus(
            ProductMetadata.Name,
            ProductMetadata.FoundationVersion,
            ".NET 10 / Razor Pages / PostgreSQL",
            true);
    }
}
