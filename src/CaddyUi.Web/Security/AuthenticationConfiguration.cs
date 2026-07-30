using Microsoft.AspNetCore.Authentication.Cookies;

namespace CaddyUi.Web.Security;

public static class AuthenticationConfiguration
{
    public static IServiceCollection AddCaddyUiAuthentication(
        this IServiceCollection services,
        SecurityRuntimeOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(securityOptions);

        services.AddSingleton(securityOptions);
        services.AddSingleton<RequestSurfaceResolver>();
        services.AddScoped<AdminCookieEvents>();

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AuthenticationSchemes.SmartAdmin;
                options.DefaultChallengeScheme = AuthenticationSchemes.SmartAdmin;
                options.DefaultSignInScheme = AuthenticationSchemes.LanAdmin;
            })
            .AddPolicyScheme(
                AuthenticationSchemes.SmartAdmin,
                AuthenticationSchemes.SmartAdmin,
                options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var resolver = context.RequestServices.GetRequiredService<RequestSurfaceResolver>();
                        return resolver.GetResolved(context) == RequestSurface.PublicAdmin
                            ? AuthenticationSchemes.PublicAdmin
                            : AuthenticationSchemes.LanAdmin;
                    };
                })
            .AddCookie(
                AuthenticationSchemes.LanAdmin,
                options => ConfigureCookie(
                    options,
                    securityOptions.LanAdminCookieName,
                    CookieSecurePolicy.SameAsRequest))
            .AddCookie(
                AuthenticationSchemes.PublicAdmin,
                options => ConfigureCookie(
                    options,
                    securityOptions.PublicAdminCookieName,
                    CookieSecurePolicy.Always));

        services.AddAuthorizationBuilder()
            .AddPolicy("Viewer", policy =>
                policy.RequireRole("admin", "editor", "viewer"))
            .AddPolicy("Editor", policy =>
                policy.RequireRole("admin", "editor"))
            .AddPolicy("Administrator", policy =>
                policy.RequireRole("admin"));

        return services;
    }

    private static void ConfigureCookie(
        CookieAuthenticationOptions options,
        string name,
        CookieSecurePolicy securePolicy)
    {
        options.Cookie.Name = name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = securePolicy;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = false;
        options.EventsType = typeof(AdminCookieEvents);
    }
}
