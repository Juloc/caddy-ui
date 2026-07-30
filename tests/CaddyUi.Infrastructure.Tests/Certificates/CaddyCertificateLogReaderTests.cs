using System.Text.Json;
using CaddyUi.Infrastructure.Certificates;

namespace CaddyUi.Infrastructure.Tests.Certificates;

public sealed class CaddyCertificateLogReaderTests
{
    [Fact]
    public void ReadReturnsRetryScheduleForWildcardCertificate()
    {
        var timestamp = DateTimeOffset.Parse("2026-07-31T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var path = WriteLog(new
        {
            level = "error",
            ts = timestamp.ToUnixTimeSeconds(),
            logger = "tls.obtain",
            msg = "will retry",
            identifier = "*.juloc.de",
            attempt = 3,
            retrying_in = 120,
            error = "could not get certificate from issuer",
        });

        try
        {
            var result = CaddyCertificateLogReader.Read(path);

            var state = Assert.Contains("*.juloc.de", result);
            Assert.Equal("retry-scheduled", state.CurrentState);
            Assert.Equal(3, state.AttemptCount);
            Assert.Equal(timestamp.AddMinutes(2), state.NextAttemptAt);
            Assert.Contains("Nächster Versuch", state.RecentEvents[0].Detail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteLogDirectory(path);
        }
    }

    [Fact]
    public void ReadMapsAcmeChallengeNameToWildcardCertificate()
    {
        var path = WriteLog(new
        {
            level = "info",
            ts = DateTimeOffset.Parse("2026-07-31T00:01:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            logger = "tls.obtain",
            msg = "waiting for DNS propagation",
            name = "_acme-challenge.juloc.de",
        });

        try
        {
            var result = CaddyCertificateLogReader.Read(path);

            var state = Assert.Contains("*.juloc.de", result);
            Assert.Equal("propagating", state.CurrentState);
            Assert.True(state.Active);
        }
        finally
        {
            DeleteLogDirectory(path);
        }
    }

    [Fact]
    public void ReadRecordsSuccessAndRedactsProviderSecrets()
    {
        var start = DateTimeOffset.Parse("2026-07-31T00:02:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var path = WriteLog(
            new
            {
                level = "error",
                ts = start.ToUnixTimeSeconds(),
                logger = "tls.obtain",
                msg = "failed to obtain certificate",
                identifier = "*.juloc.de",
                error = "provider request failed api_key=supersecret",
            },
            new
            {
                level = "info",
                ts = start.AddMinutes(1).ToUnixTimeSeconds(),
                logger = "tls.obtain",
                msg = "certificate obtained successfully",
                identifier = "*.juloc.de",
            });

        try
        {
            var result = CaddyCertificateLogReader.Read(path);

            var state = Assert.Contains("*.juloc.de", result);
            Assert.Equal("succeeded", state.CurrentState);
            Assert.Equal(start.AddMinutes(1), state.LastSuccessAt);
            Assert.Equal(0, state.ConsecutiveFailures);
            Assert.DoesNotContain("supersecret", string.Join(' ', state.RecentEvents.Select(item => item.Detail)), StringComparison.Ordinal);
        }
        finally
        {
            DeleteLogDirectory(path);
        }
    }

    private static string WriteLog(params object[] entries)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"caddy-ui-certificate-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "caddy.log");
        File.WriteAllLines(path, entries.Select(JsonSerializer.Serialize));
        return path;
    }

    private static void DeleteLogDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
