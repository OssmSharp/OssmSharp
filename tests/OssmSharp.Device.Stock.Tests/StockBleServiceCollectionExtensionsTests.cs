#if WINDOWS || MACOS
using System.Reactive.Linq;
using FastMoq.Providers.MoqProvider;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
using Shiny;
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
    [Fact]
    public void ResolvingDiscoveryAndProviderDoesNotCreateTheNativeManager()
    {
        var services = new ServiceCollection();
#if MACOS
#pragma warning disable XCODE_27_0_PREVIEW
        using var dispatchQueue = new DispatchQueue("org.ossm.stock.lazy.test");
#pragma warning restore XCODE_27_0_PREVIEW
        services.AddStockBluetooth(dispatchQueue);
#else
        services.AddStockBluetooth();
#endif
        services.AddSingleton<IBleManager>(_ => throw new InvalidOperationException(
            "The native BLE manager should remain lazy until an operation begins."));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DeviceDiscovery>(provider.GetRequiredService<IDeviceDiscovery>());
        Assert.IsType<StockDeviceProvider>(provider.GetRequiredService<IDeviceProvider>());
        var createDevice = provider.GetRequiredService<Func<DeviceIdentifier, IOssmDevice>>();
        Assert.Throws<ArgumentException>(() => createDevice(new ForeignDeviceIdentifier()));
    }

    [Fact]
    public async Task FactoryCreatesRealDeviceForOriginalSelectionAndRoutesConnectThroughNativeTransport()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var native = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        native.SetupGet(item => item.Uuid).Returns("selected");
        native.SetupGet(item => item.Status).Returns(ConnectionState.Disconnected);
        native.Setup(item => item.WhenStatusChanged())
            .Returns(Observable.Never<ConnectionState>());
        native.Setup(item => item.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        native.Setup(item => item.Connect(It.IsAny<ConnectionConfig>()))
            .Throws(new IOException("Fake native connection refused."));
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        ble.Setup(item => item.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        ble.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        var manager = mocks.GetRequiredObject<IBleManager>();
        var clock = new FakeTimeProvider();
        var nativeManagerResolutions = 0;
        var services = new ServiceCollection();
#if MACOS
#pragma warning disable XCODE_27_0_PREVIEW
        using var dispatchQueue = new DispatchQueue("org.ossm.stock.factory.test");
#pragma warning restore XCODE_27_0_PREVIEW
        services.AddStockBluetooth(dispatchQueue);
#else
        services.AddStockBluetooth();
#endif
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IBleManager>(_ =>
        {
            nativeManagerResolutions++;
            return manager;
        });
        using var provider = services.BuildServiceProvider();
        var createDevice = provider.GetRequiredService<Func<DeviceIdentifier, IOssmDevice>>();
        Assert.Equal(0, nativeManagerResolutions);
        var selected = new StockDeviceIdentifier("selected", "Device", peripheral, manager);
        await using var device = createDevice(selected);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Disconnected)
                disconnected.TrySetResult();
        };

        await ((StockOssmDevice)device).StartAsync(TestContext.Current.CancellationToken);
        await disconnected.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<StockOssmDevice>(device);
        Assert.IsType<NativeStockBleTransport>(provider.GetRequiredService<IDeviceTransport>());
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Equal(1, nativeManagerResolutions);
        ble.Verify(item => item.GetKnownPeripheral("selected"), Times.Once);
        native.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Once);
        native.Verify(item => item.CancelConnection(), Times.Once);
    }

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
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDeviceDiscovery));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDeviceProvider));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(Func<DeviceIdentifier, IOssmDevice>));
    }
#else
    [Fact]
    public void WindowsRegistrationAddsBluetoothManager()
    {
        var services = new ServiceCollection();

        var registered = services.AddStockBluetooth();

        Assert.Same(services, registered);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBleManager));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDeviceDiscovery));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDeviceProvider));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(Func<DeviceIdentifier, IOssmDevice>));
    }
#endif
}
#endif
