using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CaddyUi.Infrastructure.Certificates;

internal static class CaddyServedCertificateProbe
{
    private const string DefaultHost = "caddy";
    private const int DefaultPort = 443;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public static async Task<bool> HasValidCertificateAsync(
        string certificateName,
        string domainName,
        CancellationToken cancellationToken)
    {
        var targetName = CaddyCertificateStoreReader.NormalizeName(certificateName);
        var serverName = targetName.StartsWith("*.", StringComparison.Ordinal)
            ? $"certificate-status.{CaddyCertificateStoreReader.NormalizeName(domainName)}"
            : targetName;
        if (serverName.Length == 0)
        {
            return false;
        }

        var host = Environment.GetEnvironmentVariable("CADDY_UI_CADDY_TLS_HOST");
        host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();
        var port = int.TryParse(
            Environment.GetEnvironmentVariable("CADDY_UI_CADDY_TLS_PORT"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var configuredPort)
            ? Math.Clamp(configuredPort, 1, 65_535)
            : DefaultPort;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            await using var stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false);
            await stream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = serverName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                timeout.Token);

            if (stream.RemoteCertificate is null)
            {
                return false;
            }

            using var certificate = X509CertificateLoader.LoadCertificate(
                stream.RemoteCertificate.GetRawCertData());
            var now = DateTimeOffset.UtcNow;
            var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
            var expiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            return notBefore <= now &&
                   expiresAt > now &&
                   CaddyCertificateStoreReader.ReadDnsNames(certificate).Contains(targetName);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or AuthenticationException or
            OperationCanceledException or CryptographicException)
        {
            return false;
        }
    }
}
