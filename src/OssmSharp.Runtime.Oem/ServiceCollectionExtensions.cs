using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Registers device discovery and caller-owned device creation with the application's service collection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Shiny bluetooth, discovery, and a factory accepting a discovered identifier and returning <see cref="DeviceBase"/>.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection for composition.</returns>
    /// <remarks>
    /// Native bluetooth requires a Windows or macOS target and the host's platform permissions.
    /// The portable target accepts an explicitly registered <see cref="IBleManager"/> for tests or host composition.
    /// Registers Shiny lifecycle callbacks needed to await native cancellation, including a pending macOS connection.
    /// The factory's caller must asynchronously dispose each device, which may reconnect until disposed; it does not own the shared manager.
    /// </remarks>
    public static IServiceCollection AddOemRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IBleManager)))
        {
#if WINDOWS || MACOS
            services.AddBluetoothLE();
#else
            throw new PlatformNotSupportedException("Native bluetooth requires a Windows or macOS target, or an explicitly registered IBleManager.");
#endif
        }

        services.AddLogging();
        services
            .AddOptionsWithValidateOnStart<OssmDeviceOptions, OssmDeviceOptionsValidator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBleDelegate>(new BleLifecycleDelegate()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDeviceDiscovery, DeviceDiscovery>();

        // TODO: make a better factory method.
        services.AddSingleton<Func<string, DeviceBase>>(
            provider => identifier => new OssmDevice(
                provider.GetRequiredService<IBleManager>(),
                identifier,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<DeviceBase>>(),
                provider.GetRequiredService<IOptions<OssmDeviceOptions>>()));

        return services;
    }
}
