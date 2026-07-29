using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace CaddyUi.Infrastructure.Operations;

public sealed class NotificationDispatcher
{
    private readonly OperationsStore _store;
    private readonly ISecretReferenceResolver _secretResolver;
    private readonly IHttpClientFactory _httpClientFactory;

    public NotificationDispatcher(
        OperationsStore store,
        ISecretReferenceResolver secretResolver,
        IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _secretResolver = secretResolver;
        _httpClientFactory = httpClientFactory;
    }

    public async Task NotifyAsync(SystemNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await _store.InsertNotificationAsync(notification, cancellationToken);
        var channels = await _store.ListNotificationChannelsAsync(cancellationToken);
        foreach (var channel in channels.Where(item => item.Enabled))
        {
            try
            {
                await SendAsync(channel, notification, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The durable in-app notification is already stored. One failed channel must not block the others.
            }
        }
    }

    public async Task<ProviderOperationResult> TestChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = (await _store.ListNotificationChannelsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == channelId) ??
            throw new InvalidOperationException("The notification channel does not exist.");
        ProviderOperationResult result;
        try
        {
            await SendAsync(
                channel,
                new SystemNotification(
                    "info",
                    "notification.test",
                    "Caddy UI Testnachricht",
                    "Der Benachrichtigungskanal wurde erfolgreich aus Caddy UI getestet."),
                cancellationToken);
            result = ProviderOperationResult.Success("Test notification delivered.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = ProviderOperationResult.Failure(Limit(exception.Message, 1000));
        }

        await _store.RecordNotificationChannelTestAsync(channelId, result, cancellationToken);
        return result;
    }

    private async Task SendAsync(
        NotificationChannelRecord channel,
        SystemNotification notification,
        CancellationToken cancellationToken)
    {
        var config = OperationsJson.ReadStringObject(channel.ConfigJson);
        var references = OperationsJson.ReadStringObject(channel.SecretReferencesJson);
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in references)
        {
            secrets[pair.Key] = await _secretResolver.ResolveAsync(pair.Value, cancellationToken);
        }

        switch (channel.ChannelType)
        {
            case "webhook":
                await SendWebhookAsync(config, secrets, notification, cancellationToken);
                break;
            case "discord":
                await SendDiscordAsync(config, secrets, notification, cancellationToken);
                break;
            case "telegram":
                await SendTelegramAsync(config, secrets, notification, cancellationToken);
                break;
            case "email":
                await SendEmailAsync(config, secrets, notification, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported notification channel '{channel.ChannelType}'.");
        }
    }

    private async Task SendWebhookAsync(
        IReadOnlyDictionary<string, string> config,
        IReadOnlyDictionary<string, string> secrets,
        SystemNotification notification,
        CancellationToken cancellationToken)
    {
        var url = OperationsJson.Required(config, "url");
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireHttps(url));
        if (secrets.TryGetValue("bearer_token", out var token) && token.Length > 0)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                occurredAt = DateTimeOffset.UtcNow,
                notification.Severity,
                notification.EventType,
                notification.Title,
                notification.Message,
                notification.ObjectType,
                notification.ObjectId,
            }),
            Encoding.UTF8,
            "application/json");
        await EnsureSuccessAsync(_httpClientFactory.CreateClient("notifications"), request, cancellationToken);
    }

    private async Task SendDiscordAsync(
        IReadOnlyDictionary<string, string> config,
        IReadOnlyDictionary<string, string> secrets,
        SystemNotification notification,
        CancellationToken cancellationToken)
    {
        var url = secrets.TryGetValue("webhook_url", out var secretUrl) && secretUrl.Length > 0
            ? secretUrl
            : OperationsJson.Required(config, "webhook_url");
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireHttps(url))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    content = $"**{notification.Title}**\n{notification.Message}",
                    allowed_mentions = new { parse = Array.Empty<string>() },
                }),
                Encoding.UTF8,
                "application/json"),
        };
        await EnsureSuccessAsync(_httpClientFactory.CreateClient("notifications"), request, cancellationToken);
    }

    private async Task SendTelegramAsync(
        IReadOnlyDictionary<string, string> config,
        IReadOnlyDictionary<string, string> secrets,
        SystemNotification notification,
        CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(secrets, "bot_token");
        var chatId = OperationsJson.Required(config, "chat_id");
        var uri = RequireHttps($"https://api.telegram.org/bot{token}/sendMessage");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = $"{notification.Title}\n{notification.Message}",
                ["disable_web_page_preview"] = "true",
            }),
        };
        await EnsureSuccessAsync(_httpClientFactory.CreateClient("notifications"), request, cancellationToken);
    }

    private static async Task SendEmailAsync(
        IReadOnlyDictionary<string, string> config,
        IReadOnlyDictionary<string, string> secrets,
        SystemNotification notification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = OperationsJson.Required(config, "host");
        var port = int.TryParse(OperationsJson.Optional(config, "port", "587"), out var parsedPort)
            ? Math.Clamp(parsedPort, 1, 65535)
            : 587;
        var from = OperationsJson.Required(config, "from");
        var to = OperationsJson.Required(config, "to");
        var username = OperationsJson.Optional(config, "username");
        var password = OperationsJson.Optional(secrets, "password");
        var enableSsl = !bool.TryParse(OperationsJson.Optional(config, "enable_ssl", "true"), out var parsedSsl) || parsedSsl;

        using var message = new MailMessage(from, to)
        {
            Subject = $"[Caddy UI] {notification.Title}",
            Body = notification.Message,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = username.Length > 0 ? new NetworkCredential(username, password) : CredentialCache.DefaultNetworkCredentials,
            Timeout = 15000,
        };
        await client.SendMailAsync(message, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Notification endpoint returned HTTP {(int)response.StatusCode}: {Limit(body, 500)}");
        }
    }

    private static Uri RequireHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Notification endpoints must use HTTPS.");
        }

        return uri;
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
