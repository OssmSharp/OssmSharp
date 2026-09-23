using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OSSMDotNet.Simulator.Models;

namespace OSSMDotNet.Simulator;

/// <summary>
/// Registers the native OSSM-RS simulator as the selected device implementation.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one simulator instance as the concrete device, public device contract, and hosted service.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configure">Configures and validates the simulator executable.</param>
    /// <returns>The supplied service collection.</returns>
    /// <exception cref="InvalidOperationException">Another <see cref="IOssmDevice"/> implementation is already registered.</exception>
    public static IServiceCollection AddOssmSimulator(
        this IServiceCollection services,
        Action<SimulatedDeviceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IOssmDevice)))
        {
            throw new InvalidOperationException("An IOssmDevice implementation is already registered.");
        }

        services.AddOptions<SimulatedDeviceOptions>()
            .Configure(configure)
            .Validate(IsValid, "Simulator options are invalid.")
            .ValidateOnStart();
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SimulatedDevice>();
        services.AddSingleton<IOssmDevice>(provider => provider.GetRequiredService<SimulatedDevice>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SimulatedDevice>());
        return services;
    }

    private static bool IsValid(SimulatedDeviceOptions options) => Validator.TryValidateObject(
        options,
        new ValidationContext(options),
        null,
        validateAllProperties: true);
}
