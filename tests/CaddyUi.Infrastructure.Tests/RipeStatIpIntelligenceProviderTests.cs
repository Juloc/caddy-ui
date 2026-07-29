using System.Net;
using System.Net.Http.Headers;
using CaddyUi.Application.Security;
using CaddyUi.Domain.Security;
using CaddyUi.Infrastructure.Security;

namespace CaddyUi.Infrastructure.Tests;

public sealed class RipeStatIpIntelligenceProviderTests
{
    [Fact]
    public async Task PrivateAddress_DoesNotCallExternalProvider()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP request expected."));
        var provider = CreateProvider(handler);

        var result = await provider.LookupAsync(
            IPAddress.Parse("192.168.1.10"),
            CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(IpAddressScope.Private, result.Scope);
        Assert.Equal("local", result.Source);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PublicAddress_CombinesNetworkAndAsOverview()
    {
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = path.Contains("network-info", StringComparison.Ordinal)
                ? """
                  {
                    "status": "ok",
                    "data": {
                      "asns": [15169],
                      "prefix": "8.8.8.0/24"
                    }
                  }
                  """
                : """
                  {
                    "status": "ok",
                    "data": {
                      "holder": "GOOGLE",
                      "block": {
                        "name": "ARIN",
                        "resource": "AS15169"
                      }
                    }
                  }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json"),
                    },
                },
            };
        });
        var provider = CreateProvider(handler);

        var result = await provider.LookupAsync(
            IPAddress.Parse("8.8.8.8"),
            CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(IpAddressScope.Public, result.Scope);
        Assert.Equal("AS15169", result.Asn);
        Assert.Equal("8.8.8.0/24", result.Prefix);
        Assert.Equal("GOOGLE", result.Holder);
        Assert.Equal("ARIN", result.Registry);
        Assert.Equal("ripestat", result.Source);
        Assert.Equal(2, handler.RequestCount);
    }

    private static RipeStatIpIntelligenceProvider CreateProvider(FakeHandler handler)
    {
        var options = new IpSecurityOptions
        {
            RipeStatBaseAddress = new Uri("https://stat.ripe.net/"),
            SuccessCacheHours = 24,
            FailureCacheMinutes = 10,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = options.RipeStatBaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        return new RipeStatIpIntelligenceProvider(
            client,
            new IpAddressClassifier(),
            options,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-28T12:00:00Z")));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}
