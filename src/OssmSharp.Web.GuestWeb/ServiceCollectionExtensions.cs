namespace OssmSharp.Web.GuestWeb;

/// <summary>
/// Registers guest-session services for the web host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped guest session client and circuit lifecycle handler.
    /// </summary>
    /// <param name="services">The guest web host service collection.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddGuestSession(this IServiceCollection services)
    {
        services.AddGuestControlClient("OSSM:ControlClient");
        services.AddScoped<CircuitHandler, GuestCircuitHandler>();
        return services;
    }
}
