#if WINDOWS || MACOS
using Microsoft.Extensions.DependencyInjection;
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
#pragma warning disable XCODE_27_0_PREVIEW // Xcode 27 preview bindings mark even longstanding Apple APIs experimental.
    public static IServiceCollection AddStockBluetooth(
        this IServiceCollection services,
        DispatchQueue dispatchQueue)
#pragma warning restore XCODE_27_0_PREVIEW
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        return services.AddBluetoothLE(new AppleBleConfiguration(DispatchQueue: dispatchQueue));
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
        return services.AddBluetoothLE();
    }
#endif
}
#endif
