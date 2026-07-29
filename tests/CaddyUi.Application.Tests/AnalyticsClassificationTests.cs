using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;

namespace CaddyUi.Application.Tests;

public sealed class AnalyticsClassificationTests
{
    private readonly CaddyAccessLogParser _parser = new();
    private readonly RequestClassifier _classifier = new();

    [Fact]
    public void BrowserDocument_IsOnePageView()
    {
        var request = Parse(
            """
            {
              "ts": 1785261600.125,
              "request": {
                "client_ip": "203.0.113.10",
                "method": "GET",
                "host": "mealie.example.com",
                "uri": "/recipes?token=secret-value&sort=name",
                "headers": {
                  "User-Agent": ["Mozilla/5.0 Chrome/140.0"],
                  "Accept": ["text/html,application/xhtml+xml"],
                  "Sec-Fetch-Dest": ["document"],
                  "Authorization": ["Bearer secret"]
                }
              },
              "duration": 0.012,
              "size": 2048,
              "status": 200,
              "resp_headers": {
                "Content-Type": ["text/html; charset=utf-8"],
                "Set-Cookie": ["secret=value"]
              }
            }
            """);

        var result = _classifier.Classify(request);

        Assert.Equal(AnalyticsActorType.Human, result.ActorType);
        Assert.Equal(AnalyticsRequestType.Document, result.RequestType);
        Assert.True(result.IsNavigation);
        Assert.True(result.IsPageView);
        Assert.Equal("token=[redacted]&sort=name", request.QueryString);
        Assert.DoesNotContain("secret-value", request.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer secret", request.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=value", request.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void NuxtAsset_IsARequestButNotAPageView()
    {
        var request = Parse(
            """
            {
              "ts": 1785261601,
              "request": {
                "client_ip": "203.0.113.10",
                "method": "GET",
                "host": "mealie.example.com",
                "uri": "/_nuxt/entry.4f94cda7.js",
                "headers": {
                  "User-Agent": ["Mozilla/5.0 Chrome/140.0"],
                  "Accept": ["*/*"],
                  "Sec-Fetch-Dest": ["script"]
                }
              },
              "duration": 0.002,
              "size": 4096,
              "status": 200,
              "resp_headers": {
                "Content-Type": ["application/javascript"]
              }
            }
            """);

        var result = _classifier.Classify(request);

        Assert.Equal(AnalyticsActorType.Human, result.ActorType);
        Assert.Equal(AnalyticsRequestType.Asset, result.RequestType);
        Assert.False(result.IsNavigation);
        Assert.False(result.IsPageView);
    }

    [Fact]
    public void RedirectedDocument_IsNavigationButNotPageView()
    {
        var request = Parse(
            """
            {
              "ts": 1785261602,
              "request": {
                "client_ip": "203.0.113.10",
                "method": "GET",
                "host": "mealie.example.com",
                "uri": "/old",
                "headers": {
                  "User-Agent": ["Mozilla/5.0 Firefox/140.0"],
                  "Accept": ["text/html"],
                  "Sec-Fetch-Dest": ["document"]
                }
              },
              "duration": 0.001,
              "size": 0,
              "status": 308
            }
            """);

        var result = _classifier.Classify(request);

        Assert.True(result.IsNavigation);
        Assert.False(result.IsPageView);
        Assert.Equal(AnalyticsNavigationState.Redirected, result.NavigationState);
    }

    [Fact]
    public void Scanner_IsBotAndNeverAPageView()
    {
        var request = Parse(
            """
            {
              "ts": 1785261603,
              "request": {
                "remote_ip": "198.51.100.7",
                "method": "GET",
                "host": "mealie.example.com",
                "uri": "/",
                "headers": {
                  "User-Agent": ["ExampleScannerBot/1.0"],
                  "Accept": ["text/html"]
                }
              },
              "duration": 0.01,
              "size": 512,
              "status": 200,
              "resp_headers": {
                "Content-Type": ["text/html"]
              }
            }
            """);

        var result = _classifier.Classify(request);

        Assert.Equal(AnalyticsActorType.Bot, result.ActorType);
        Assert.Equal(AnalyticsRequestType.Document, result.RequestType);
        Assert.False(result.IsNavigation);
        Assert.False(result.IsPageView);
    }

    [Fact]
    public void Fingerprint_MarksProxyOnlyClientAsEstimated()
    {
        var request = Parse(
            """
            {
              "ts": 1785261604,
              "request": {
                "client_ip": "192.168.1.1",
                "method": "GET",
                "host": "mealie.example.com",
                "uri": "/",
                "headers": {
                  "User-Agent": ["Mozilla/5.0 Chrome/140.0"],
                  "Accept": ["text/html"],
                  "Sec-Fetch-Dest": ["document"]
                }
              },
              "duration": 0.01,
              "size": 512,
              "status": 200
            }
            """);

        var identity = AnalyticsClientFingerprint.Create(
            request,
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

        Assert.NotNull(identity);
        Assert.True(identity.Estimated);
        Assert.StartsWith("estimated:", identity.ClientKey, StringComparison.Ordinal);
    }

    private NormalizedRequestEvent Parse(string json)
    {
        Assert.True(
            _parser.TryParse(
                json,
                "/logs/access.log",
                0,
                out var request,
                out var error),
            error);
        return Assert.IsType<NormalizedRequestEvent>(request);
    }
}
