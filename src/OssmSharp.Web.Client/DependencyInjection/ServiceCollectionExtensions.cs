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
        AddClientOptions(services, configurationSection, guest: false);
        services.AddScoped<UserControlClient>();
        return services;
    }

    /// <summary>
    /// Adds a guest client that connects only after receiving an invitation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">The settings section for the API URL and connection timings, without a service key.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddGuestControlClient(this IServiceCollection services, string configurationSection)
    {
        AddClientOptions(services, configurationSection, guest: true);
        services.AddScoped<GuestControlClient>();
        return services;
    }

    private static void AddClientOptions(IServiceCollection services, string configurationSection, bool guest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationSection);
        services.AddOptions<ControlClientOptions>()
            .BindConfiguration(configurationSection, options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate(
                options => guest ? string.IsNullOrEmpty(options.ServiceKey) : !string.IsNullOrWhiteSpace(options.ServiceKey),
                guest ? "Guest clients must not configure a ServiceKey." : "Administrator clients require a ServiceKey.")
            .ValidateOnStart();
        services.AddSingleton<ControlHubConnectionFactory>();
    }
}
