using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Client.DependencyInjection;

/// <summary>
/// Registers validated, circuit-scoped control-service clients.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one typed server-side control connection for each consumer scope.
    /// </summary>
    /// <param name="services">The service collection receiving the clients.</param>
    /// <param name="configurationSection">The section containing the trusted control origin and service key.</param>
    /// <returns>The same service collection for further registration.</returns>
    public static IServiceCollection AddAdminApiClient(this IServiceCollection services, string configurationSection)
    {
        AddClientOptions(services, configurationSection, guest: false);
        services.AddScoped<AdminApiClient>();
        services.AddScoped<IAdminApiClient>(provider => provider.GetRequiredService<AdminApiClient>());
        return services;
    }

    /// <summary>
    /// Adds a guest client that connects only after receiving an invitation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">The settings section for the API URL and connection timings, without a service key.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddGuestApiClient(this IServiceCollection services, string configurationSection)
    {
        AddClientOptions(services, configurationSection, guest: true);
        services.AddScoped<GuestApiClient>();
        services.AddScoped<IGuestApiClient>(provider => provider.GetRequiredService<GuestApiClient>());
        return services;
    }

    private static void AddClientOptions(IServiceCollection services, string configurationSection, bool guest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationSection);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RpcClientOptions>()
            .BindConfiguration(configurationSection, options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate(
                options => guest ? string.IsNullOrEmpty(options.ServiceKey) : !string.IsNullOrWhiteSpace(options.ServiceKey),
                guest ? "Guest clients must not configure a ServiceKey." : "Administrator clients require a ServiceKey.")
            .ValidateOnStart();
        services.AddSingleton<SignalRHubConnectionFactory>();
        services.AddSingleton<IHubConnectionFactory>(provider => provider.GetRequiredService<SignalRHubConnectionFactory>());
        services.AddScoped<IRpcClient>(provider => new SignalRRpcClient(
            provider.GetRequiredService<IHubConnectionFactory>(),
            provider.GetRequiredService<IOptions<RpcClientOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<SignalRRpcClient>>()));
    }
}
