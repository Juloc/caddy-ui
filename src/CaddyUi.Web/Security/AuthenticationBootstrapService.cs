using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Security;

namespace CaddyUi.Web.Security;

public sealed class AuthenticationBootstrapService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly AuthenticationStore _store;
    private readonly PasswordHashService _passwords;
    private readonly ILogger<AuthenticationBootstrapService> _logger;

    public AuthenticationBootstrapService(
        IConfiguration configuration,
        AuthenticationStore store,
        PasswordHashService passwords,
        ILogger<AuthenticationBootstrapService> logger)
    {
        _configuration = configuration;
        _store = store;
        _passwords = passwords;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var password = _configuration["CADDY_UI_PASSWORD"] ??
            _configuration["Authentication:BootstrapPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation(
                "No bootstrap password is configured. Existing PostgreSQL users remain authoritative.");
            return;
        }

        var username = (_configuration["CADDY_UI_USERNAME"] ??
            _configuration["Authentication:BootstrapUsername"] ??
            "admin").Trim();
        var ensurePassword = ReadBoolean(
            _configuration["CADDY_UI_ENSURE_BOOTSTRAP_PASSWORD"] ??
            _configuration["Authentication:EnsureBootstrapPassword"]);

        var existing = await _store.FindUserByUsernameAsync(username, stoppingToken);
        if (existing is not null)
        {
            if (ensurePassword)
            {
                var verification = _passwords.Verify(password, existing.PasswordHash);
                if (!verification.Succeeded)
                {
                    await _store.UpdatePasswordHashAsync(
                        existing.Id,
                        _passwords.HashPassword(password),
                        stoppingToken);
                    _logger.LogWarning(
                        "Reconciled the configured bootstrap password for existing administrator {Username}. Disable Authentication:EnsureBootstrapPassword after the isolated shadow test.",
                        username);
                }
            }

            return;
        }

        if (await _store.CountUsersAsync(stoppingToken) > 0)
        {
            _logger.LogWarning(
                "Bootstrap user {Username} was not created because PostgreSQL already contains other users.",
                username);
            return;
        }

        await _store.CreateUserAsync(
            username,
            username,
            _passwords.HashPassword(password),
            "admin",
            stoppingToken);
        _logger.LogWarning(
            "Created the initial administrator {Username} from bootstrap configuration. Remove the bootstrap password after the first successful login.",
            username);
    }

    private static bool ReadBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }
}
