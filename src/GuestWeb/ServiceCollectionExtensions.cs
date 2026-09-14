using Microsoft.AspNetCore.Components.Server.Circuits;
using OSSMDotNet.Web.GuestWeb.Services;

namespace OSSMDotNet.Web.GuestWeb;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGuestSession(this IServiceCollection services)
    {
        services.AddScoped<GuestSessionClient>();
        services.AddScoped<CircuitHandler, GuestCircuitHandler>();
        return services;
    }
}
