#if WINDOWS || MACOS
using System.Reactive.Linq;
using FastMoq.Providers.MoqProvider;
using Moq;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class NativeStockBleTransportTests
{
    [Fact]
    public async Task KnownPeripheralConnectsWithoutScanning()
    {
        using var mocks = new Mocker();
        var manager = ArrangeManager(mocks);
        var (peripheral, native, disconnect) = ArrangePeripheral(mocks, "selected");
        manager.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        var selected = new StockDeviceIdentifier(
            "selected", "Device", peripheral, mocks.GetRequiredObject<IBleManager>());
        var discovery = mocks.GetRequiredObject<IDeviceDiscovery>();

        var connection = await new NativeStockBleTransport(mocks.GetRequiredObject<IBleManager>(), discovery)
            .ConnectAsync(selected, StockFirmwareProfile.Service, TestContext.Current.CancellationToken);
        disconnect();
        await connection.DisposeAsync();

        Assert.IsType<NativeStockBleConnection>(connection);
        native.Verify(item => item.Connect(It.Is<ConnectionConfig>(config => !config.AutoConnect)), Times.Once);
        native.Verify(item => item.CancelConnection(), Times.Once);
        mocks.Verify<IDeviceDiscovery>(
            item => item.ScanAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task UnknownPeripheralScansOnceAndConnectsOnlyTheExactSelectedId()
    {
        using var mocks = new Mocker();
        var manager = ArrangeManager(mocks);
        var (selectedPeripheral, _, _) = ArrangePeripheral(mocks, "Selected");
        var (unrelated, otherNative, _) = ArrangePeripheral(mocks, "different");
        var (found, foundNative, disconnect) = ArrangePeripheral(mocks, "SELECTED");
        var owner = mocks.GetRequiredObject<IBleManager>();
        var selected = new StockDeviceIdentifier("Selected", "Same name", selectedPeripheral, owner);
        var discovery = mocks.GetOrCreateMock<IDeviceDiscovery>().AsMoq();
        var token = TestContext.Current.CancellationToken;
        discovery.Setup(item => item.ScanAsync(token))
            .Returns(Task.FromResult<IReadOnlyList<DeviceIdentifier>>(
            [
                new StockDeviceIdentifier("different", "Same name", unrelated, owner),
                new StockDeviceIdentifier("SELECTED", "Same name", found, owner),
            ]));

        var connection = await new NativeStockBleTransport(owner, mocks.GetRequiredObject<IDeviceDiscovery>())
            .ConnectAsync(selected, StockFirmwareProfile.Service, token);
        disconnect();
        await connection.DisposeAsync();

        manager.Verify(item => item.GetKnownPeripheral("Selected"), Times.Once);
        mocks.Verify<IDeviceDiscovery>(item => item.ScanAsync(token), TimesSpec.Once);
        foundNative.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Once);
        otherNative.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
    }

    [Fact]
    public async Task MissingSelectedPeripheralCannotFallBackToDifferentDevice()
    {
        using var mocks = new Mocker();
        ArrangeManager(mocks);
        var (selectedPeripheral, selectedNative, _) = ArrangePeripheral(mocks, "selected");
        var (otherPeripheral, otherNative, _) = ArrangePeripheral(mocks, "different");
        var manager = mocks.GetRequiredObject<IBleManager>();
        var selected = new StockDeviceIdentifier("selected", "Same name", selectedPeripheral, manager);
        mocks.GetOrCreateMock<IDeviceDiscovery>().AsMoq()
            .Setup(item => item.ScanAsync(TestContext.Current.CancellationToken))
            .Returns(Task.FromResult<IReadOnlyList<DeviceIdentifier>>(
                [new StockDeviceIdentifier("different", "Same name", otherPeripheral, manager)]));

        await Assert.ThrowsAsync<IOException>(() =>
            new NativeStockBleTransport(manager, mocks.GetRequiredObject<IDeviceDiscovery>())
                .ConnectAsync(selected, StockFirmwareProfile.Service, TestContext.Current.CancellationToken));

        selectedNative.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
        otherNative.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
        mocks.Verify<IDeviceDiscovery>(
            item => item.ScanAsync(TestContext.Current.CancellationToken), TimesSpec.Once);
    }

    [Fact]
    public async Task KnownPeripheralWithMismatchedUuidIsNotConnected()
    {
        using var mocks = new Mocker();
        var manager = ArrangeManager(mocks);
        var (selectedPeripheral, _, _) = ArrangePeripheral(mocks, "selected");
        var (wrong, wrongNative, _) = ArrangePeripheral(mocks, "different");
        manager.Setup(item => item.GetKnownPeripheral("selected")).Returns(wrong);
        var selected = new StockDeviceIdentifier(
            "selected", "Device", selectedPeripheral, mocks.GetRequiredObject<IBleManager>());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NativeStockBleTransport(
                mocks.GetRequiredObject<IBleManager>(), mocks.GetRequiredObject<IDeviceDiscovery>())
                .ConnectAsync(selected, StockFirmwareProfile.Service, TestContext.Current.CancellationToken));

        wrongNative.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
        mocks.Verify<IDeviceDiscovery>(
            item => item.ScanAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOrDifferentCompositionIdentifierFailsBeforePermissionAndScan(bool foreignType)
    {
        using var mocks = new Mocker();
        var managerMock = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var manager = mocks.GetRequiredObject<IBleManager>();
        var (peripheral, native, _) = ArrangePeripheral(mocks, "selected");
        DeviceIdentifier selected = foreignType
            ? new ForeignDeviceIdentifier()
            : new StockDeviceIdentifier(
                "selected", "Device", peripheral, mocks.CreateStandaloneFastMock<IBleManager>().Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new NativeStockBleTransport(manager, mocks.GetRequiredObject<IDeviceDiscovery>())
                .ConnectAsync(selected, StockFirmwareProfile.Service, TestContext.Current.CancellationToken));

        managerMock.Verify(item => item.RequestAccess(), Times.Never);
        managerMock.Verify(item => item.GetKnownPeripheral(It.IsAny<string>()), Times.Never);
        native.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
        mocks.Verify<IDeviceDiscovery>(
            item => item.ScanAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task FailedConnectDisposesNativeConnectionAndPreservesFailure()
    {
        using var mocks = new Mocker();
        var manager = ArrangeManager(mocks);
        var (peripheral, native, _) = ArrangePeripheral(mocks, "selected");
        manager.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        native.Setup(item => item.Connect(It.IsAny<ConnectionConfig>()))
            .Throws(new IOException("Native connect failed."));
        var selected = new StockDeviceIdentifier(
            "selected", "Device", peripheral, mocks.GetRequiredObject<IBleManager>());

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            new NativeStockBleTransport(
                mocks.GetRequiredObject<IBleManager>(), mocks.GetRequiredObject<IDeviceDiscovery>())
                .ConnectAsync(selected, StockFirmwareProfile.Service, TestContext.Current.CancellationToken));

        Assert.Contains("Native connect failed", failure.Message, StringComparison.Ordinal);
        native.Verify(item => item.Connect(It.IsAny<ConnectionConfig>()), Times.Once);
        native.Verify(item => item.CancelConnection(), Times.Once);
    }

    private static Mock<IBleManager> ArrangeManager(Mocker mocks)
    {
        var manager = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        manager.Setup(item => item.RequestAccess())
            .Returns(Observable.Return(AccessState.Available));
        return manager;
    }

    private static (IPeripheral Peripheral, Mock<IPeripheral> Native, Action Disconnect)
        ArrangePeripheral(Mocker mocks, string id)
    {
        var handle = mocks.CreateStandaloneFastMock<IPeripheral>();
        var native = handle.AsMoq();
        var state = ConnectionState.Disconnected;
        native.SetupGet(item => item.Uuid).Returns(id);
        native.SetupGet(item => item.Status).Returns(() => state);
        native.Setup(item => item.WhenStatusChanged())
            .Returns(Observable.Return(ConnectionState.Connected));
        native.Setup(item => item.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        native.Setup(item => item.Connect(It.IsAny<ConnectionConfig>()))
            .Callback(() => state = ConnectionState.Connected);
        return (handle.Instance, native, () => state = ConnectionState.Disconnected);
    }
}
#endif
