using Microsoft.AspNetCore.Components.Server.Circuits;
using OSSMDotNet.GuestWeb.Services;

namespace OSSMDotNet.GuestWeb;

/// <summary>Registers guest-session services for the web host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the scoped guest session client and circuit lifecycle handler.</summary>
    public static IServiceCollection AddGuestSession(this IServiceCollection services)
    {
        services.AddScoped<GuestSessionClient>();
        services.AddScoped<CircuitHandler, GuestCircuitHandler>();
        return services;
    }
}
