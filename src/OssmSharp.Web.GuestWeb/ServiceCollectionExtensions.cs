using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace OssmSharp.Web.GuestWeb;

/// <summary>
/// Registers guest-session services for the web host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers fixed-expiry protected browser-session cookies, antiforgery validation, and scope-owned guest clients.
    /// </summary>
    /// <param name="services">The guest web host service collection.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddGuestSession(this IServiceCollection services)
    {
        services.AddGuestApiClient("OSSM:ControlClient");
        services.AddDataProtection().SetApplicationName("OssmSharp.GuestWeb");
        services.AddAuthentication(GuestSessionEndpoints.Scheme)
            .AddCookie(GuestSessionEndpoints.Scheme, options =>
            {
                options.Cookie.Name = GuestSessionEndpoints.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(24);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddOptions<CookieAuthenticationOptions>(GuestSessionEndpoints.Scheme)
            .Configure<TimeProvider>((options, time) =>
            {
                options.TimeProvider = time;
                options.Events.OnValidatePrincipal = context =>
                {
                    if (context.Properties.ExpiresUtc is not { } expiry || expiry <= time.GetUtcNow())
                        context.RejectPrincipal();
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        services.AddScoped<CircuitHandler, GuestCircuitHandler>();
        return services;
    }
}
