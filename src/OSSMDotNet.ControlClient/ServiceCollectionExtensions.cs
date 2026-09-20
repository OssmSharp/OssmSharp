using Microsoft.Extensions.DependencyInjection;

namespace OSSMDotNet.ControlClient;

/// <summary>Registers validated, circuit-scoped control-service clients.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers one typed server-side control connection for each consumer scope.</summary>
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
