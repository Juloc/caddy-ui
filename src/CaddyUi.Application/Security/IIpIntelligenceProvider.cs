using System.Net;
using CaddyUi.Domain.Security;

namespace CaddyUi.Application.Security;

public interface IIpIntelligenceProvider
{
    Task<IpIntelligenceResult> LookupAsync(
        IPAddress address,
        CancellationToken cancellationToken);
}
