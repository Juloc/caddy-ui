using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class NotificationsModel : PageModel
{
    private readonly OperationsStore _store;
    private readonly NotificationDispatcher _notifications;

    public NotificationsModel(OperationsStore store, NotificationDispatcher notifications)
    {
        _store = store;
        _notifications = notifications;
    }

    public IReadOnlyList<NotificationChannelRecord> Channels { get; private set; } = Array.Empty<NotificationChannelRecord>();

    [BindProperty]
    public ChannelInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Channels = await _store.ListNotificationChannelsAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            Channels = await _store.ListNotificationChannelsAsync(HttpContext.RequestAborted);
            return Page();
        }

        try
        {
            await _store.CreateNotificationChannelAsync(
                Input.Name,
                Input.ChannelType,
                Input.ConfigJson,
                Input.SecretReferencesJson,
                HttpContext.RequestAborted);
            TempData["Message"] = "Benachrichtigungskanal wurde angelegt.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Channels = await _store.ListNotificationChannelsAsync(HttpContext.RequestAborted);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid channelId, bool enabled)
    {
        await _store.SetNotificationChannelEnabledAsync(channelId, enabled, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid channelId)
    {
        var result = await _notifications.TestChannelAsync(channelId, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public sealed class ChannelInput
    {
        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string ChannelType { get; set; } = "webhook";

        [Required]
        public string ConfigJson { get; set; } = "{}";

        [Required]
        public string SecretReferencesJson { get; set; } = "{}";
    }
}
