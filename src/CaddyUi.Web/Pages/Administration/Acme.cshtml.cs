using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class AcmeModel : PageModel
{
    private readonly AcmeEmailPreferenceService _service;

    public AcmeModel(AcmeEmailPreferenceService service)
    {
        _service = service;
    }

    [BindProperty]
    public AcmeInput Input { get; set; } = new();

    public AcmeEmailState State { get; private set; } =
        new(string.Empty, UsesEnvironmentVariable: false);

    public string LoadError { get; private set; } = string.Empty;

    [TempData]
    public string StatusMessage { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        await LoadAsync(populateInput: true);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(populateInput: false);
            return Page();
        }

        try
        {
            var result = await _service.UpdateAsync(
                Input.Email,
                HttpContext.RequestAborted);
            StatusMessage = result.Changed
                ? result.UsesEnvironmentVariable
                    ? "Das UI-Feld ist leer. ACME_EMAIL aus der Umgebung wird verwendet."
                    : result.Email.Length == 0
                        ? "Das UI-Feld ist leer und ACME_EMAIL ist nicht verfügbar. Die globale ACME-E-Mail wurde entfernt."
                        : "Die ACME-E-Mail wurde über die UI gespeichert und dauerhaft übernommen."
                : result.UsesEnvironmentVariable
                    ? "ACME_EMAIL aus der Umgebung wurde bereits verwendet."
                    : "Die ACME-E-Mail war bereits unverändert konfiguriert.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(populateInput: false);
            return Page();
        }
    }

    private async Task LoadAsync(bool populateInput)
    {
        try
        {
            State = await _service.ReadAsync(HttpContext.RequestAborted);
            if (populateInput)
            {
                Input.Email = State.Email;
            }

            LoadError = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            State = new AcmeEmailState(string.Empty, UsesEnvironmentVariable: false);
            LoadError = exception.Message;
        }
    }

    public sealed class AcmeInput
    {
        [MaxLength(254)]
        [EmailAddress(ErrorMessage = "Bitte eine gültige E-Mail-Adresse eingeben.")]
        public string Email { get; set; } = string.Empty;
    }
}
