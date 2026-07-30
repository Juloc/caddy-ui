using System.ComponentModel.DataAnnotations;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Access;

[Authorize(Policy = "Administrator")]
public sealed class IndexModel : PageModel
{
    private readonly RouteManagementStore _store;
    private readonly PasswordHashService _passwordHashService;

    public IndexModel(
        RouteManagementStore store,
        PasswordHashService passwordHashService)
    {
        _store = store;
        _passwordHashService = passwordHashService;
    }

    public IReadOnlyList<AccessGroupRecord> Groups { get; private set; } =
        Array.Empty<AccessGroupRecord>();

    public IReadOnlyList<AccessCredentialRecord> Credentials { get; private set; } =
        Array.Empty<AccessCredentialRecord>();

    public string? LoadError { get; private set; }

    [BindProperty]
    public GroupInput NewGroup { get; set; } = new();

    [BindProperty]
    public CredentialInput NewCredential { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateGroupAsync()
    {
        ModelState.ClearValidationState(nameof(NewCredential));
        if (!TryValidateModel(NewGroup, nameof(NewGroup)))
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            await _store.CreateAccessGroupAsync(
                NewGroup.Name,
                NewGroup.Description,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = "Zugriffsgruppe angelegt.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleGroupAsync(Guid id, bool enabled)
    {
        try
        {
            await _store.SetAccessGroupEnabledAsync(id, enabled, HttpContext.RequestAborted);
            StatusMessage = enabled ? "Zugriffsgruppe aktiviert." : "Zugriffsgruppe deaktiviert.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateCredentialAsync()
    {
        ModelState.ClearValidationState(nameof(NewGroup));
        if (!TryValidateModel(NewCredential, nameof(NewCredential)))
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            var passwordHash = _passwordHashService.HashPassword(NewCredential.Password);
            await _store.CreateCredentialAsync(
                NewCredential.GroupId,
                NewCredential.Username,
                passwordHash,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = "Portal-Zugang angelegt. Das Kennwort wird nicht erneut angezeigt.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            NewCredential.Password = string.Empty;
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleCredentialAsync(Guid id, bool enabled)
    {
        try
        {
            await _store.SetCredentialEnabledAsync(id, enabled, HttpContext.RequestAborted);
            StatusMessage = enabled ? "Portal-Zugang aktiviert." : "Portal-Zugang deaktiviert.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        try
        {
            Groups = await _store.ListAccessGroupsAsync(HttpContext.RequestAborted);
            Credentials = await _store.ListCredentialsAsync(cancellationToken: HttpContext.RequestAborted);
            LoadError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Groups = Array.Empty<AccessGroupRecord>();
            Credentials = Array.Empty<AccessCredentialRecord>();
            LoadError = $"Zugriffsgruppen konnten nicht geladen werden: {exception.Message}";
        }
    }

    public sealed class GroupInput
    {
        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;
    }

    public sealed class CredentialInput
    {
        [Required]
        public Guid GroupId { get; set; }

        [Required]
        [MaxLength(120)]
        public string Username { get; set; } = string.Empty;

        [Required]
        [MinLength(12)]
        [MaxLength(1024)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
