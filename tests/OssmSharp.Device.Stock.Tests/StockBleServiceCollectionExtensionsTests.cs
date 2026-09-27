#if WINDOWS || MACOS
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE;
#if MACOS
using CoreFoundation;
#endif

namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Ensures the host can register one platform BLE manager without opening a device.
/// </summary>
public sealed class StockBleServiceCollectionExtensionsTests
{
#if MACOS
    [Fact]
    public void MacOSRegistrationUsesCallerOwnedDispatchQueue()
    {
#pragma warning disable XCODE_27_0_PREVIEW
        using var dispatchQueue = new DispatchQueue("org.ossm.stock.test");
#pragma warning restore XCODE_27_0_PREVIEW
        var services = new ServiceCollection();

        var registered = services.AddStockBluetooth(dispatchQueue);

        Assert.Same(services, registered);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBleManager));
    }
#else
    [Fact]
    public void WindowsRegistrationAddsBluetoothManager()
    {
        var services = new ServiceCollection();

        var registered = services.AddStockBluetooth();

        Assert.Same(services, registered);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBleManager));
    }
#endif
}
#endif
