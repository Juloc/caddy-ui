from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, content: str) -> None:
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8", newline="\n")


def replace_once(path: str, old: str, new: str) -> None:
    content = read(path)
    count = content.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}: {old[:120]!r}")
    write(path, content.replace(old, new, 1))


write(
    "src/CaddyUi.Infrastructure/Routing/AccessGroupPresentation.cs",
    '''using System.Text.Json;

namespace CaddyUi.Infrastructure.Routing;

public sealed record AccessGroupPresentation(string AccentColor, string IconUrl)
{
    public const string DefaultAccentColor = "#0F6CBD";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public string EffectiveAccentColor =>
        string.IsNullOrWhiteSpace(AccentColor) ? DefaultAccentColor : AccentColor;

    public static AccessGroupPresentation Create(
        string? accentColor,
        string? iconUrl)
    {
        return new AccessGroupPresentation(
            NormalizeAccentColor(accentColor),
            NormalizeIconUrl(iconUrl));
    }

    public static AccessGroupPresentation FromJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return Create(null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Create(null, null);
            }

            var accentColor = document.RootElement.TryGetProperty(
                "accentColor",
                out var accentElement)
                    ? accentElement.GetString()
                    : null;
            var iconUrl = document.RootElement.TryGetProperty(
                "iconUrl",
                out var iconElement)
                    ? iconElement.GetString()
                    : null;
            return Create(accentColor, iconUrl);
        }
        catch (JsonException)
        {
            return Create(null, null);
        }
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(
            new
            {
                accentColor = AccentColor,
                iconUrl = IconUrl,
            },
            JsonOptions);
    }

    private static string NormalizeAccentColor(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (candidate.Length != 7 ||
            candidate[0] != '#' ||
            !candidate.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Accent color must be empty or use the hexadecimal format #RRGGBB.");
        }

        return candidate.ToUpperInvariant();
    }

    private static string NormalizeIconUrl(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (candidate.Length > 2048 ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Icon URL must be empty or an absolute HTTPS URL.");
        }

        return uri.AbsoluteUri;
    }
}
''')

write(
    "src/CaddyUi.Infrastructure/Persistence/Migrations/20260801190000_AccessPortalPresentationAndLoginScope.cs",
    '''using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260801190000_AccessPortalPresentationAndLoginScope")]
public sealed class AccessPortalPresentationAndLoginScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.login_attempts
                ALTER COLUMN scope TYPE text;
            ALTER TABLE caddy_ui.login_blocks
                ALTER COLUMN scope TYPE text;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM caddy_ui.login_attempts WHERE length(scope) > 24;
            DELETE FROM caddy_ui.login_blocks WHERE length(scope) > 24;
            ALTER TABLE caddy_ui.login_attempts
                ALTER COLUMN scope TYPE character varying(24);
            ALTER TABLE caddy_ui.login_blocks
                ALTER COLUMN scope TYPE character varying(24);
            """);
    }
}
''')

write(
    "src/CaddyUi.Web/wwwroot/portal/portal.css",
    ''':root {
    color-scheme: light dark;
    font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
    --portal-accent: #0f6cbd;
    --portal-background: #f5f5f5;
    --portal-surface: #ffffff;
    --portal-surface-secondary: #fafafa;
    --portal-text: #242424;
    --portal-text-secondary: #616161;
    --portal-border: #d1d1d1;
    --portal-border-strong: #8a8886;
    --portal-danger: #c50f1f;
    --portal-shadow: 0 8px 28px rgb(0 0 0 / 14%);
}

* {
    box-sizing: border-box;
}

html,
body {
    min-height: 100%;
    margin: 0;
}

body {
    background:
        radial-gradient(circle at top, color-mix(in srgb, var(--portal-accent) 12%, transparent), transparent 42rem),
        var(--portal-background);
    color: var(--portal-text);
}

.portal-shell {
    display: grid;
    min-height: 100vh;
    place-items: center;
    padding: 32px 20px;
}

.portal-card {
    width: min(100%, 440px);
    overflow: hidden;
    border: 1px solid var(--portal-border);
    border-radius: 12px;
    background: var(--portal-surface);
    box-shadow: var(--portal-shadow);
}

.portal-accent {
    height: 4px;
    background: var(--portal-accent);
}

.portal-content {
    display: grid;
    gap: 24px;
    padding: 32px;
}

.portal-brand {
    display: flex;
    align-items: center;
    gap: 14px;
}

.portal-icon {
    display: grid;
    width: 48px;
    height: 48px;
    flex: 0 0 48px;
    place-items: center;
    overflow: hidden;
    border-radius: 10px;
    background: color-mix(in srgb, var(--portal-accent) 14%, var(--portal-surface));
    color: var(--portal-accent);
}

.portal-icon img {
    width: 100%;
    height: 100%;
    object-fit: cover;
}

.portal-icon svg {
    width: 26px;
    height: 26px;
    fill: currentColor;
}

.portal-brand-copy {
    display: grid;
    gap: 2px;
}

.portal-brand-copy strong {
    font-size: 16px;
    font-weight: 600;
    line-height: 22px;
}

.portal-brand-copy span,
.portal-description {
    color: var(--portal-text-secondary);
    font-size: 14px;
    line-height: 20px;
}

.portal-heading {
    display: grid;
    gap: 8px;
}

.portal-eyebrow {
    margin: 0;
    color: var(--portal-accent);
    font-size: 12px;
    font-weight: 600;
    letter-spacing: .04em;
    line-height: 16px;
    text-transform: uppercase;
}

.portal-heading h1 {
    margin: 0;
    font-size: 28px;
    font-weight: 600;
    line-height: 36px;
}

.portal-description {
    margin: 0;
}

.portal-form {
    display: grid;
    gap: 18px;
}

.portal-field {
    display: grid;
    gap: 6px;
}

.portal-field span {
    font-size: 14px;
    font-weight: 600;
    line-height: 20px;
}

.portal-field input {
    width: 100%;
    min-height: 40px;
    border: 1px solid var(--portal-border-strong);
    border-radius: 4px;
    background: var(--portal-surface);
    color: var(--portal-text);
    font: inherit;
    padding: 8px 11px;
    transition: border-color 100ms ease, box-shadow 100ms ease;
}

.portal-field input:hover {
    border-color: var(--portal-text);
}

.portal-field input:focus-visible {
    border-color: var(--portal-accent);
    box-shadow: 0 0 0 2px color-mix(in srgb, var(--portal-accent) 28%, transparent);
    outline: none;
}

.portal-button {
    min-height: 40px;
    border: 1px solid var(--portal-accent);
    border-radius: 4px;
    background: var(--portal-accent);
    color: #ffffff;
    cursor: pointer;
    font: inherit;
    font-size: 14px;
    font-weight: 600;
    padding: 8px 18px;
    transition: filter 100ms ease, transform 100ms ease;
}

.portal-button:hover {
    filter: brightness(.94);
}

.portal-button:active {
    transform: translateY(1px);
}

.portal-button:focus-visible {
    box-shadow:
        0 0 0 2px var(--portal-surface),
        0 0 0 4px var(--portal-accent);
    outline: none;
}

.portal-validation:empty {
    display: none;
}

.portal-validation {
    border-left: 3px solid var(--portal-danger);
    border-radius: 2px;
    background: color-mix(in srgb, var(--portal-danger) 8%, var(--portal-surface));
    color: var(--portal-danger);
    font-size: 14px;
    line-height: 20px;
    padding: 10px 12px;
}

.portal-validation ul {
    margin: 0;
    padding-left: 20px;
}

.portal-footer {
    border-top: 1px solid var(--portal-border);
    background: var(--portal-surface-secondary);
    color: var(--portal-text-secondary);
    font-size: 12px;
    line-height: 16px;
    padding: 14px 32px;
}

@media (max-width: 520px) {
    .portal-shell {
        align-items: stretch;
        padding: 0;
    }

    .portal-card {
        width: 100%;
        min-height: 100vh;
        border: 0;
        border-radius: 0;
        box-shadow: none;
    }

    .portal-content {
        padding: 28px 22px;
    }

    .portal-footer {
        margin-top: auto;
        padding-inline: 22px;
    }
}

@media (prefers-color-scheme: dark) {
    :root {
        --portal-background: #1b1a19;
        --portal-surface: #292827;
        --portal-surface-secondary: #252423;
        --portal-text: #ffffff;
        --portal-text-secondary: #d1d1d1;
        --portal-border: #484644;
        --portal-border-strong: #8a8886;
        --portal-shadow: 0 12px 36px rgb(0 0 0 / 42%);
    }
}

@media (prefers-reduced-motion: reduce) {
    *,
    *::before,
    *::after {
        scroll-behavior: auto !important;
        transition-duration: 0.01ms !important;
    }
}
''')

write(
    "src/CaddyUi.Web/Pages/Portal/Login.cshtml",
    '''@page "/__caddy_ui_auth/login"
@model CaddyUi.Web.Pages.Portal.LoginModel
@{
    Layout = null;
    var culture = global::System.Globalization.CultureInfo.CurrentUICulture.Name;
}
<!DOCTYPE html>
<html lang="@culture">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="light dark" />
    <title>@Model.GroupName · @T["Protected access"]</title>
    <link rel="stylesheet" href="/__caddy_ui_auth/assets/portal.css" />
</head>
<body style="--portal-accent: @Model.AccentColor">
    <main class="portal-shell">
        <section class="portal-card" aria-labelledby="portal-title">
            <div class="portal-accent" aria-hidden="true"></div>
            <div class="portal-content">
                <div class="portal-brand">
                    <span class="portal-icon" aria-hidden="true">
                        @if (!string.IsNullOrWhiteSpace(Model.IconUrl))
                        {
                            <img src="@Model.IconUrl" alt="" referrerpolicy="no-referrer" />
                        }
                        else
                        {
                            <svg viewBox="0 0 24 24" focusable="false">
                                <path d="M12 2.2 20 5v5.8c0 5.1-3.3 9.4-8 11-4.7-1.6-8-5.9-8-11V5l8-2.8Zm0 2.1L6 6.4v4.4c0 4 2.4 7.5 6 8.9 3.6-1.4 6-4.9 6-8.9V6.4l-6-2.1Zm0 3.2a3 3 0 0 1 3 3v1.1h.5c.8 0 1.5.7 1.5 1.5v3.4c0 .8-.7 1.5-1.5 1.5h-7c-.8 0-1.5-.7-1.5-1.5v-3.4c0-.8.7-1.5 1.5-1.5H9v-1.1a3 3 0 0 1 3-3Zm0 1.8c-.7 0-1.2.5-1.2 1.2v1.1h2.4v-1.1c0-.7-.5-1.2-1.2-1.2Z" />
                            </svg>
                        }
                    </span>
                    <div class="portal-brand-copy">
                        <strong>Caddy Access Portal</strong>
                        <span>@T["Protected application"]</span>
                    </div>
                </div>

                <div class="portal-heading">
                    <p class="portal-eyebrow">@T["Access group"]</p>
                    <h1 id="portal-title">@Model.GroupName</h1>
                    <p class="portal-description">@T["This application requires a valid portal session for the assigned group."]</p>
                </div>

                <div asp-validation-summary="ModelOnly" class="portal-validation" role="alert"></div>

                <form method="post" class="portal-form">
                    <input type="hidden" asp-for="Group" />
                    <input type="hidden" asp-for="ReturnTo" />
                    <label class="portal-field">
                        <span>@T["Username"]</span>
                        <input asp-for="Input.Username" autocomplete="username" autofocus />
                    </label>
                    <label class="portal-field">
                        <span>@T["Password"]</span>
                        <input asp-for="Input.Password" type="password" autocomplete="current-password" />
                    </label>
                    <button class="portal-button" type="submit">@T["Sign in"]</button>
                </form>
            </div>
            <footer class="portal-footer">@T["Authentication is provided by the protected application's access group."]</footer>
        </section>
    </main>
</body>
</html>
''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementModels.cs",
    '''public sealed record AccessGroupRecord(
    Guid Id,
    string Name,
    string Description,
    bool Enabled,
    int CredentialCount,
    int RouteCount,
    DateTimeOffset UpdatedAt);''',
    '''public sealed record AccessGroupRecord(
    Guid Id,
    string Name,
    string Description,
    string AccentColor,
    string IconUrl,
    bool Enabled,
    int CredentialCount,
    int RouteCount,
    DateTimeOffset UpdatedAt);''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementStore.cs",
    '''                   COUNT(DISTINCT routes.id)::integer,
                   groups.updated_at
            FROM caddy_ui.access_groups AS groups''',
    '''                   COUNT(DISTINCT routes.id)::integer,
                   groups.config_json::text,
                   groups.updated_at
            FROM caddy_ui.access_groups AS groups''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementStore.cs",
    '''        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AccessGroupRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                ReadTimestamp(reader, 6)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AccessCredentialRecord>> ListCredentialsAsync''',
    '''        while (await reader.ReadAsync(cancellationToken))
        {
            var presentation = AccessGroupPresentation.FromJson(reader.GetString(6));
            result.Add(new AccessGroupRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                presentation.AccentColor,
                presentation.IconUrl,
                reader.GetBoolean(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                ReadTimestamp(reader, 7)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AccessCredentialRecord>> ListCredentialsAsync''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementStore.cs",
    '''    public async Task<Guid> CreateAccessGroupAsync(
        string name,
        string description,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedName = Required(name, 120, "Group name");
        var normalizedDescription = Limit(description?.Trim() ?? string.Empty, 500);
        var id = Guid.NewGuid();''',
    '''    public Task<Guid> CreateAccessGroupAsync(
        string name,
        string description,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        return CreateAccessGroupAsync(
            name,
            description,
            accentColor: null,
            iconUrl: null,
            actor,
            cancellationToken);
    }

    public async Task<Guid> CreateAccessGroupAsync(
        string name,
        string description,
        string? accentColor,
        string? iconUrl,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedName = Required(name, 120, "Group name");
        var normalizedDescription = Limit(description?.Trim() ?? string.Empty, 500);
        var presentation = AccessGroupPresentation.Create(accentColor, iconUrl);
        var id = Guid.NewGuid();''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementStore.cs",
    '''                INSERT INTO caddy_ui.access_groups(
                    id, name, config_json, created_at, updated_at, enabled, description)
                VALUES(@id, @name, '{}'::jsonb, @now, @now, true, @description)
                """;''',
    '''                INSERT INTO caddy_ui.access_groups(
                    id, name, config_json, created_at, updated_at, enabled, description)
                VALUES(
                    @id, @name, CAST(@config_json AS jsonb),
                    @now, @now, true, @description)
                """;''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementStore.cs",
    '''            AddParameter(command, "description", normalizedDescription);
            AddParameter(command, "now", now);''',
    '''            AddParameter(command, "description", normalizedDescription);
            AddParameter(command, "config_json", presentation.ToJson());
            AddParameter(command, "now", now);''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/RouteManagementStore.cs",
    '''                JsonSerializer.Serialize(new { name = normalizedName, description = normalizedDescription }, JsonOptions),''',
    '''                JsonSerializer.Serialize(
                    new
                    {
                        name = normalizedName,
                        description = normalizedDescription,
                        presentation.AccentColor,
                        presentation.IconUrl,
                    },
                    JsonOptions),''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''    public async Task UpdateGroupAsync(
        Guid groupId,
        string name,
        string description,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedName = Required(name, 120, "Group name");
        var normalizedDescription = Limit(description?.Trim() ?? string.Empty, 500);''',
    '''    public Task UpdateGroupAsync(
        Guid groupId,
        string name,
        string description,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        return UpdateGroupAsync(
            groupId,
            name,
            description,
            accentColor: null,
            iconUrl: null,
            actor,
            cancellationToken);
    }

    public async Task UpdateGroupAsync(
        Guid groupId,
        string name,
        string description,
        string? accentColor,
        string? iconUrl,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedName = Required(name, 120, "Group name");
        var normalizedDescription = Limit(description?.Trim() ?? string.Empty, 500);''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''            var previous = await ReadGroupAsync(connection, transaction, groupId, cancellationToken) ??
                throw new InvalidOperationException("The selected access group no longer exists.");
            await using var command = connection.CreateCommand();''',
    '''            var previous = await ReadGroupAsync(connection, transaction, groupId, cancellationToken) ??
                throw new InvalidOperationException("The selected access group no longer exists.");
            var presentation = accentColor is null && iconUrl is null
                ? AccessGroupPresentation.FromJson(previous.ConfigJson)
                : AccessGroupPresentation.Create(accentColor, iconUrl);
            await using var command = connection.CreateCommand();''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''                SET name = @name,
                    description = @description,
                    updated_at = @now''',
    '''                SET name = @name,
                    description = @description,
                    config_json = CAST(@config_json AS jsonb),
                    updated_at = @now''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''            AddParameter(command, "description", normalizedDescription);
            AddParameter(command, "now", DateTimeOffset.UtcNow);''',
    '''            AddParameter(command, "description", normalizedDescription);
            AddParameter(command, "config_json", presentation.ToJson());
            AddParameter(command, "now", DateTimeOffset.UtcNow);''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''                        description = normalizedDescription,
                        previous.Enabled,''',
    '''                        description = normalizedDescription,
                        presentation.AccentColor,
                        presentation.IconUrl,
                        previous.Enabled,''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''                   groups.enabled,
                   (SELECT COUNT(*)::integer''',
    '''                   groups.enabled,
                   groups.config_json::text,
                   (SELECT COUNT(*)::integer''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetInt32(3),
                reader.GetInt32(4))''',
    '''                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5))''')

replace_once(
    "src/CaddyUi.Infrastructure/Routing/AccessAdministrationStore.cs",
    '''    private sealed record GroupState(
        string Name,
        string Description,
        bool Enabled,
        int CredentialCount,
        int RouteCount);''',
    '''    private sealed record GroupState(
        string Name,
        string Description,
        bool Enabled,
        string ConfigJson,
        int CredentialCount,
        int RouteCount);''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml.cs",
    '''                    Name = group.Name,
                    Description = group.Description,''',
    '''                    Name = group.Name,
                    Description = group.Description,
                    AccentColor = group.AccentColor,
                    IconUrl = group.IconUrl,''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml.cs",
    '''                NewGroup.Name,
                NewGroup.Description,
                User.ToManagementActor(HttpContext),''',
    '''                NewGroup.Name,
                NewGroup.Description,
                NewGroup.AccentColor,
                NewGroup.IconUrl,
                User.ToManagementActor(HttpContext),''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml.cs",
    '''                EditGroup.Name,
                EditGroup.Description,
                User.ToManagementActor(HttpContext),''',
    '''                EditGroup.Name,
                EditGroup.Description,
                EditGroup.AccentColor,
                EditGroup.IconUrl,
                User.ToManagementActor(HttpContext),''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml.cs",
    '''        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;
    }

    public sealed class CredentialInput''',
    '''        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;

        [MaxLength(7)]
        [RegularExpression(
            @"^$|^#[0-9A-Fa-f]{6}$",
            ErrorMessage = "Use the hexadecimal format #RRGGBB.")]
        public string AccentColor { get; set; } = string.Empty;

        [MaxLength(2048)]
        [Url]
        public string IconUrl { get; set; } = string.Empty;
    }

    public sealed class CredentialInput''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml.cs",
    '''        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;
    }

    public sealed class CredentialEditInput''',
    '''        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;

        [MaxLength(7)]
        [RegularExpression(
            @"^$|^#[0-9A-Fa-f]{6}$",
            ErrorMessage = "Use the hexadecimal format #RRGGBB.")]
        public string AccentColor { get; set; } = string.Empty;

        [MaxLength(2048)]
        [Url]
        public string IconUrl { get; set; } = string.Empty;
    }

    public sealed class CredentialEditInput''')

replace_once(
    "src/CaddyUi.Web/Pages/Portal/Login.cshtml.cs",
    '''    public string GroupName { get; private set; } = "Geschützter Zugriff";

    public async Task<IActionResult> OnGetAsync()''',
    '''    public string GroupName { get; private set; } = "Geschützter Zugriff";

    public string AccentColor { get; private set; } =
        AccessGroupPresentation.DefaultAccentColor;

    public string IconUrl { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()''')

replace_once(
    "src/CaddyUi.Web/Pages/Portal/Login.cshtml.cs",
    '''        GroupName = group.Name;
        return true;''',
    '''        var presentation = AccessGroupPresentation.FromJson(group.ConfigJson);
        GroupName = group.Name;
        AccentColor = presentation.EffectiveAccentColor;
        IconUrl = presentation.IconUrl;
        return true;''')

replace_once(
    "src/CaddyUi.Web/Program.cs",
    '''using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;''',
    '''using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;''')

replace_once(
    "src/CaddyUi.Web/Program.cs",
    '''app.UseMiddleware<RequestSurfaceMiddleware>();
app.UseStaticFiles();
app.UseRouting();''',
    '''app.UseMiddleware<RequestSurfaceMiddleware>();
var portalAssetRoot = Path.Combine(
    app.Environment.WebRootPath ??
        Path.Combine(app.Environment.ContentRootPath, "wwwroot"),
    "portal");
app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(portalAssetRoot),
        RequestPath = "/__caddy_ui_auth/assets",
    });
app.UseStaticFiles();
app.UseRouting();''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml",
    '''                    <label class="field"><span class="field-label">@T["Description"]</span><textarea asp-for="NewGroup.Description" rows="4"></textarea></label>
                </fieldset>''',
    '''                    <label class="field"><span class="field-label">@T["Description"]</span><textarea asp-for="NewGroup.Description" rows="4"></textarea></label>
                    <label class="field"><span class="field-label">@T["Accent color"]</span><input asp-for="NewGroup.AccentColor" placeholder="#0F6CBD" autocomplete="off" /><span asp-validation-for="NewGroup.AccentColor" class="field-error"></span><small>@T["Optional hexadecimal color in the format #RRGGBB."]</small></label>
                    <label class="field"><span class="field-label">@T["Icon URL"]</span><input asp-for="NewGroup.IconUrl" type="url" placeholder="https://…" autocomplete="off" /><span asp-validation-for="NewGroup.IconUrl" class="field-error"></span><small>@T["Optional HTTPS image URL for the access portal."]</small></label>
                </fieldset>''')

replace_once(
    "src/CaddyUi.Web/Pages/Access/Index.cshtml",
    '''                <label class="field"><span class="field-label">@T["Description"]</span><textarea asp-for="EditGroup.Description" rows="4"></textarea></label>
            </form>''',
    '''                <label class="field"><span class="field-label">@T["Description"]</span><textarea asp-for="EditGroup.Description" rows="4"></textarea></label>
                <label class="field"><span class="field-label">@T["Accent color"]</span><input asp-for="EditGroup.AccentColor" placeholder="#0F6CBD" autocomplete="off" /><span asp-validation-for="EditGroup.AccentColor" class="field-error"></span><small>@T["Optional hexadecimal color in the format #RRGGBB."]</small></label>
                <label class="field"><span class="field-label">@T["Icon URL"]</span><input asp-for="EditGroup.IconUrl" type="url" placeholder="https://…" autocomplete="off" /><span asp-validation-for="EditGroup.IconUrl" class="field-error"></span><small>@T["Optional HTTPS image URL for the access portal."]</small></label>
            </form>''')

replace_once(
    "tests/CaddyUi.Migration.Tests/MigrationDiscoveryTests.cs",
    '''        Assert.Contains("20260731200000_MultilingualUserPreferences", migrations);''',
    '''        Assert.Contains("20260731200000_MultilingualUserPreferences", migrations);
        Assert.Contains(
            "20260801190000_AccessPortalPresentationAndLoginScope",
            migrations);''')

replace_once(
    "tests/CaddyUi.Infrastructure.Tests/PostgreSqlMigrationTests.cs",
    '''using CaddyUi.Infrastructure.Routing;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;''',
    '''using CaddyUi.Infrastructure.Routing;
using CaddyUi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;''')

replace_once(
    "tests/CaddyUi.Infrastructure.Tests/PostgreSqlMigrationTests.cs",
    '''        Assert.Contains("20260728270000_PhaseSevenRouteManagement", appliedMigrations);

        var routeStore''',
    '''        Assert.Contains("20260728270000_PhaseSevenRouteManagement", appliedMigrations);
        Assert.Contains(
            "20260801190000_AccessPortalPresentationAndLoginScope",
            appliedMigrations);

        var routeStore''')

replace_once(
    "tests/CaddyUi.Infrastructure.Tests/PostgreSqlMigrationTests.cs",
    '''        Assert.True(accessGroupColumnsExist);

        var routeAccessGroupForeignKeyExists''',
    '''        Assert.True(accessGroupColumnsExist);

        var loginScopeColumnsAreText = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT COUNT(*) = 2 AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'caddy_ui'
                  AND table_name IN ('login_attempts', 'login_blocks')
                  AND column_name = 'scope'
                  AND data_type = 'text'
                """)
            .SingleAsync();
        Assert.True(loginScopeColumnsAreText);

        var authenticationStore = new AuthenticationStore(
            new RuntimeDbContextFactory(_postgres.GetConnectionString()));
        await authenticationStore.RecordLoginAttemptAsync(
            $"portal:{Guid.NewGuid():D}",
            "portal-test",
            "127.0.0.1",
            succeeded: true,
            reason: string.Empty);

        var routeAccessGroupForeignKeyExists''')

replace_once(
    "tests/CaddyUi.Web.Tests/PortalRouteMarkupTests.cs",
    '''        Assert.DoesNotContain(
            "@page \\"/portal/authorize\\"",
            authorizeMarkup,
            StringComparison.Ordinal);''',
    '''        Assert.DoesNotContain(
            "@page \\"/portal/authorize\\"",
            authorizeMarkup,
            StringComparison.Ordinal);
        Assert.Contains(
            "/__caddy_ui_auth/assets/portal.css",
            loginMarkup,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "~/css/site.css",
            loginMarkup,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "~/js/theme-init.js",
            loginMarkup,
            StringComparison.Ordinal);''')

write(
    "tests/CaddyUi.Infrastructure.Tests/AccessGroupPresentationTests.cs",
    '''using CaddyUi.Infrastructure.Routing;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AccessGroupPresentationTests
{
    [Fact]
    public void Create_NormalizesValidOptionalPresentation()
    {
        var presentation = AccessGroupPresentation.Create(
            "#0f6cbd",
            "https://example.test/icon.png");

        Assert.Equal("#0F6CBD", presentation.AccentColor);
        Assert.Equal(
            "https://example.test/icon.png",
            presentation.IconUrl);
        Assert.Equal("#0F6CBD", presentation.EffectiveAccentColor);
    }

    [Fact]
    public void FromJson_UsesSafeDefaultsForInvalidLegacyConfiguration()
    {
        var presentation = AccessGroupPresentation.FromJson(
            """{"accentColor":"red","iconUrl":"javascript:alert(1)"}""");

        Assert.Empty(presentation.AccentColor);
        Assert.Empty(presentation.IconUrl);
        Assert.Equal(
            AccessGroupPresentation.DefaultAccentColor,
            presentation.EffectiveAccentColor);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#123")]
    [InlineData("#12345678")]
    public void Create_RejectsInvalidAccentColor(string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => AccessGroupPresentation.Create(value, null));
    }

    [Theory]
    [InlineData("http://example.test/icon.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/icon.png")]
    public void Create_RejectsNonHttpsIconUrl(string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => AccessGroupPresentation.Create(null, value));
    }
}
''')

write(
    "tests/CaddyUi.Web.Tests/PortalPresentationContractTests.cs",
    '''namespace CaddyUi.Web.Tests;

public sealed class PortalPresentationContractTests
{
    [Fact]
    public void PortalUsesDedicatedFluentSurfaceAndReservedAssetPath()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Portal/Login.cshtml"));
        var styles = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/wwwroot/portal/portal.css"));
        var program = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Program.cs"));

        Assert.Contains("portal-card", markup, StringComparison.Ordinal);
        Assert.Contains("--portal-accent", markup, StringComparison.Ordinal);
        Assert.Contains("portal-button", markup, StringComparison.Ordinal);
        Assert.Contains(
            "/__caddy_ui_auth/assets/portal.css",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequestPath = \\"/__caddy_ui_auth/assets\\"",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "\\"Segoe UI Variable Text\\"",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "@media (prefers-color-scheme: dark)",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "@media (prefers-reduced-motion: reduce)",
            styles,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"Repository file '{relativePath}' could not be located.");
    }
}
''')

resource_path = "src/CaddyUi.Web/Resources/SharedResource.de.resx"
resource = read(resource_path)
entries = '''  <data name="Accent color" xml:space="preserve"><value>Akzentfarbe</value></data>
  <data name="Optional hexadecimal color in the format #RRGGBB." xml:space="preserve"><value>Optionale Hexadezimalfarbe im Format #RRGGBB.</value></data>
  <data name="Icon URL" xml:space="preserve"><value>Icon-URL</value></data>
  <data name="Optional HTTPS image URL for the access portal." xml:space="preserve"><value>Optionale HTTPS-Bildadresse für das Zugriffsportal.</value></data>
  <data name="Authentication is provided by the protected application's access group." xml:space="preserve"><value>Die Anmeldung erfolgt über die Zugriffsgruppe der geschützten Anwendung.</value></data>
'''
if 'name="Accent color"' not in resource:
    resource = resource.replace("</root>", entries + "</root>")
    write(resource_path, resource)
