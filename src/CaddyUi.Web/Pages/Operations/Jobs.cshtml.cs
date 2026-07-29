using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class JobsModel : PageModel
{
    private readonly OperationsStore _store;
    private readonly OperationsCommandService _commands;

    public JobsModel(OperationsStore store, OperationsCommandService commands)
    {
        _store = store;
        _commands = commands;
    }

    public IReadOnlyList<ScheduledJobRecord> Jobs { get; private set; } = Array.Empty<ScheduledJobRecord>();
    public IReadOnlyList<JobRunRecord> Runs { get; private set; } = Array.Empty<JobRunRecord>();

    [BindProperty]
    public JobInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            await _store.CreateJobAsync(
                Input.Name,
                Input.JobType,
                Input.IntervalSeconds,
                Input.ConfigJson,
                HttpContext.RequestAborted);
            TempData["Message"] = "Job wurde angelegt.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid jobId, bool enabled)
    {
        await _store.SetJobEnabledAsync(jobId, enabled, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunAsync(Guid jobId)
    {
        var result = await _commands.RunJobAsync(jobId, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Jobs = await _store.ListJobsAsync(HttpContext.RequestAborted);
        Runs = await _store.ListJobRunsAsync(100, HttpContext.RequestAborted);
    }

    public sealed class JobInput
    {
        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string JobType { get; set; } = "health";

        [Range(60, 604800)]
        public int IntervalSeconds { get; set; } = 300;

        [Required]
        public string ConfigJson { get; set; } = "{}";
    }
}
