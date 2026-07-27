namespace CaddyUi.Migration;

public sealed record MigrationCommandLine(
    string Command,
    string? SourcePath,
    string BackupDirectory,
    string? ReportPath,
    bool DryRun,
    bool IncludeRawRequests,
    bool ShowHelp)
{
    public static MigrationCommandLine Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return new MigrationCommandLine(
                "schema",
                null,
                DefaultBackupDirectory(),
                null,
                DryRun: false,
                IncludeRawRequests: false,
                ShowHelp: false);
        }

        var command = arguments[0].Trim().ToLowerInvariant();
        if (command is "-h" or "--help" or "help")
        {
            return Help();
        }

        string? sourcePath = null;
        string? reportPath = null;
        var backupDirectory = DefaultBackupDirectory();
        var dryRun = false;
        var includeRawRequests = false;

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--include-raw-requests":
                    includeRawRequests = true;
                    break;
                case "--source":
                    sourcePath = NextValue(arguments, ref index, argument);
                    break;
                case "--backup-dir":
                    backupDirectory = NextValue(arguments, ref index, argument);
                    break;
                case "--report":
                    reportPath = NextValue(arguments, ref index, argument);
                    break;
                case "-h":
                case "--help":
                    return Help();
                default:
                    throw new ArgumentException(
                        $"Unknown migration argument '{argument}'.",
                        nameof(arguments));
            }
        }

        if (command is "import" or "verify" or "inspect")
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException(
                    $"Command '{command}' requires --source <path>.",
                    nameof(arguments));
            }
        }
        else if (command != "schema")
        {
            throw new ArgumentException(
                $"Unknown migration command '{command}'.",
                nameof(arguments));
        }

        return new MigrationCommandLine(
            command,
            sourcePath,
            backupDirectory,
            reportPath,
            dryRun,
            includeRawRequests,
            ShowHelp: false);
    }

    public static string Usage =>
        """
        Caddy UI migration commands:

          migrate schema
              Apply PostgreSQL migrations.

          migrate inspect --source <legacy.db> [--report <report.json>]
              Inspect the legacy SQLite database without writing PostgreSQL.

          migrate import --source <legacy.db> [--backup-dir <directory>]
                         [--report <report.json>] [--dry-run]
                         [--include-raw-requests]
              Create a consistent SQLite backup and import it idempotently.

          migrate verify --source <consistent-backup.db> [--report <report.json>]
              Verify source counts and import keys for a successful migration.
        """;

    private static MigrationCommandLine Help()
    {
        return new MigrationCommandLine(
            "help",
            null,
            DefaultBackupDirectory(),
            null,
            DryRun: false,
            IncludeRawRequests: false,
            ShowHelp: true);
    }

    private static string NextValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        index++;
        if (index >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException(
                $"Option '{option}' requires a value.",
                nameof(arguments));
        }

        return arguments[index];
    }

    private static string DefaultBackupDirectory()
    {
        return Environment.GetEnvironmentVariable("CADDY_UI_BACKUP_DIR") ??
            "/var/lib/caddy-ui/backups";
    }
}
