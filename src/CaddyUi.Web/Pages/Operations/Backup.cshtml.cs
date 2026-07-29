using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class BackupModel : PageModel
{
    private readonly OperationsStore _store;
    private readonly BackupDiagnosticsService _backups;
    private readonly OperationsOptions _options;

    public BackupModel(
        OperationsStore store,
        BackupDiagnosticsService backups,
        OperationsOptions options)
    {
        _store = store;
        _backups = backups;
        _options = options;
    }

    public IReadOnlyList<BackupArtifactRecord> Backups { get; private set; } = Array.Empty<BackupArtifactRecord>();

    public async Task OnGetAsync()
    {
        Backups = await _store.ListBackupsAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var result = await _backups.CreateBackupAsync(HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetDiagnosticsAsync()
    {
        var artifact = await _backups.CreateDiagnosticsAsync(HttpContext.RequestAborted);
        return File(artifact.Content, artifact.ContentType, artifact.FileName);
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id)
    {
        var artifact = (await _store.ListBackupsAsync(HttpContext.RequestAborted))
            .FirstOrDefault(item => item.Id == id) ??
            throw new InvalidOperationException("Das Backup existiert nicht mehr.");
        var root = Path.GetFullPath(_options.BackupDirectory);
        var path = Path.GetFullPath(artifact.Path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "application/zip", artifact.FileName);
    }
}
