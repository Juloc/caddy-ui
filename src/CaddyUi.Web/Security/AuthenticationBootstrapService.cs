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

        if (await _store.CountUsersAsync(stoppingToken) > 0)
        {
            return;
        }

        var username = _configuration["CADDY_UI_USERNAME"] ??
            _configuration["Authentication:BootstrapUsername"] ??
            "admin";
        await _store.CreateUserAsync(
            username,
            username,
            _passwords.HashPassword(password),
            "admin",
            stoppingToken);
        _logger.LogWarning(
            "Created the initial administrator {Username} from bootstrap configuration. Remove CADDY_UI_PASSWORD after the first successful login.",
            username);
    }
}
