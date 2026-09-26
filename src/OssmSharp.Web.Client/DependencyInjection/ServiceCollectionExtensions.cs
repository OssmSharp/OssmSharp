using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Client.DependencyInjection;

/// <summary>Registers validated, circuit-scoped control-service clients.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one typed server-side control connection for each consumer scope.
    /// </summary>
    /// <param name="services">The service collection receiving the clients.</param>
    /// <param name="configurationSection">The section containing the trusted control origin and service key.</param>
    /// <returns>The same service collection for further registration.</returns>
    public static IServiceCollection AddControlClient(this IServiceCollection services, string configurationSection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationSection);
        services.AddOptions<ControlClientOptions>()
            .BindConfiguration(configurationSection, options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ControlHubConnectionFactory>();
        services.AddScoped<OperatorControlClient>();
        services.AddScoped<GuestControlClient>();
        return services;
    }
}
