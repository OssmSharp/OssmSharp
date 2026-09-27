#if WINDOWS || MACOS
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Runtime;
using Shiny;
using Shiny.BluetoothLE;
#if MACOS
using CoreFoundation;
#endif

namespace OssmSharp.Device.Stock;

/// <summary>
/// Registers the native BLE manager used by stock discovery and connection.
/// </summary>
public static class StockBleServiceCollectionExtensions
{
#if MACOS
    /// <summary>
    /// Registers Shiny's macOS BLE manager on an explicitly owned CoreBluetooth dispatch queue.
    /// </summary>
    /// <param name="services">The owning host's service registrations.</param>
    /// <param name="dispatchQueue">
    /// A queue retained by the host until after its service provider and BLE operations are disposed.
    /// The service collection does not take ownership of this queue.
    /// </param>
    /// <returns>The same service collection for composition.</returns>
    public static IServiceCollection AddStockBluetooth(
        this IServiceCollection services,
        DispatchQueue dispatchQueue)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        services.AddBluetoothLE(new AppleBleConfiguration(DispatchQueue: dispatchQueue));
        return services.AddStockDeviceServices();
    }
#else
    /// <summary>
    /// Registers Shiny's Windows BLE manager without starting a scan or connection.
    /// </summary>
    /// <param name="services">The owning host's service registrations.</param>
    /// <returns>The same service collection for composition.</returns>
    public static IServiceCollection AddStockBluetooth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBluetoothLE();
        return services.AddStockDeviceServices();
    }
#endif

    private static IServiceCollection AddStockDeviceServices(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(provider => new Lazy<IBleManager>(
            () => provider.GetRequiredService<IBleManager>()));
        services.AddSingleton<DeviceDiscovery>();
        services.AddSingleton<IDeviceDiscovery>(provider => provider.GetRequiredService<DeviceDiscovery>());
        services.AddSingleton<IDeviceProvider, StockDeviceProvider>();
        services.AddSingleton<IStockQualificationResolver, UnqualifiedStockMotion>();
        services.AddSingleton<IDeviceTransport>(provider =>
            new NativeStockBleTransport(
                provider.GetRequiredService<Lazy<IBleManager>>().Value,
                provider.GetRequiredService<IDeviceDiscovery>()));
        services.AddSingleton<Func<DeviceIdentifier, IOssmDevice>>(provider => identifier =>
        {
            if (identifier is not StockDeviceIdentifier)
            {
                throw new ArgumentException("The selected device does not belong to this stock BLE composition.", nameof(identifier));
            }

            StockDeviceIdentifier.Validate(identifier, provider.GetRequiredService<Lazy<IBleManager>>().Value);
            return new StockOssmDevice(
                Options.Create(new DeviceRuntimeOptions()),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<StockOssmDevice>>(),
                identifier,
                provider.GetRequiredService<IDeviceTransport>(),
                provider.GetRequiredService<IStockQualificationResolver>());
        });
        return services;
    }
}
#endif
