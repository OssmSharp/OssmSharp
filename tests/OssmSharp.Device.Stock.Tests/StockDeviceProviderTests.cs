#if WINDOWS || MACOS
using System.Text;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using FastMoq.Providers.MoqProvider;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class StockDeviceProviderTests
{
    [Fact]
    public async Task ForeignIdentifierFailsBeforeNativeAccess()
    {
        using var mocks = new Mocker();
        var manager = mocks.GetRequiredObject<IBleManager>();
        var provider = CreateProvider(new Lazy<IBleManager>(() => manager));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetDeviceAsync(
            new ForeignDeviceIdentifier(), TestContext.Current.CancellationToken));
        mocks.Verify<IBleManager>(ble => ble.RequestAccess(), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task UnavailableSelectedPeripheralDoesNotConnectToAnotherDevice()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess())
            .Returns(Observable.Return(AccessState.Available));
        var peripheralMock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        peripheralMock.SetupGet(peripheral => peripheral.Uuid).Returns("selected");
        var manager = mocks.GetRequiredObject<IBleManager>();
        var identifier = new StockDeviceIdentifier(
            "selected", "Chosen", mocks.GetRequiredObject<IPeripheral>(), manager);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(new Lazy<IBleManager>(() => manager))
                .GetDeviceAsync(identifier, TestContext.Current.CancellationToken));

        Assert.Contains("no longer available", failure.Message, StringComparison.Ordinal);
        mocks.Verify<IBleManager>(item => item.GetKnownPeripheral("selected"), TimesSpec.Once);
        mocks.Verify<IPeripheral>(item => item.Connect(FastArg.Any<ConnectionConfig>()),
            TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task DeniedPermissionPreventsIdentityConnection()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Denied));
        var peripheralMock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        peripheralMock.SetupGet(peripheral => peripheral.Uuid).Returns("selected");
        var manager = mocks.GetRequiredObject<IBleManager>();
        var identifier = new StockDeviceIdentifier(
            "selected", "Chosen", mocks.GetRequiredObject<IPeripheral>(), manager);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(new Lazy<IBleManager>(() => manager))
                .GetDeviceAsync(identifier, TestContext.Current.CancellationToken));
        mocks.Verify<IBleManager>(item => item.GetKnownPeripheral("selected"), TimesSpec.NeverCalled);
        mocks.Verify<IPeripheral>(item => item.Connect(FastArg.Any<ConnectionConfig>()),
            TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task ReadReturnsSelectedIdentifierAndParsedIdentityThenDisconnects()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var device = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        device.SetupGet(peripheral => peripheral.Uuid).Returns("selected");
        device.SetupGet(peripheral => peripheral.Status).Returns(ConnectionState.Connected);
        device.Setup(peripheral => peripheral.WhenStatusChanged())
            .Returns(Observable.Return(ConnectionState.Disconnected));
        device.Setup(peripheral => peripheral.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        ble.Setup(manager => manager.RequestAccess())
            .Returns(Observable.Return(AccessState.Available));
        ble.Setup(manager => manager.GetKnownPeripheral("selected")).Returns(peripheral);
        device.Setup(item => item.ReadCharacteristic(
            StockFirmwareProfile.Service.ToString(),
            StockFirmwareProfile.Identity.ToString()))
            .Returns(Observable.Return(new BleCharacteristicResult(
                null!, BleCharacteristicEvent.Read,
                Encoding.UTF8.GetBytes("aa:bb:cc:dd:ee:ff;ESP32;1;0123456789abcdef0123456789abcdef;1.0.34"))));
        var identifier = new StockDeviceIdentifier(
            "selected", "Advertised", peripheral, mocks.GetRequiredObject<IBleManager>());

        var result = await CreateProvider(new Lazy<IBleManager>(
                () => mocks.GetRequiredObject<IBleManager>()))
            .GetDeviceAsync(identifier, TestContext.Current.CancellationToken);

        Assert.Same(identifier, result.Identifier);
        Assert.Equal(identifier.Id, result.Identity);
        Assert.Null(result.ProfileId);
        Assert.Empty(result.SupportedPatterns);
        Assert.Equal("AA:BB:CC:DD:EE:FF", result.WifiMac);
        Assert.Equal("ESP32", result.Chip);
        Assert.True(result.WifiConnected);
        Assert.Equal("0123456789abcdef0123456789abcdef", result.SketchMd5);
        Assert.Equal("1.0.34", result.Version);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task FailedDisconnectIsReportedAsUnsettledCleanup()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var device = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        device.SetupGet(item => item.Uuid).Returns("selected");
        device.SetupGet(item => item.Status).Returns(ConnectionState.Connected);
        device.Setup(item => item.WhenStatusChanged())
            .Returns(Observable.Never<ConnectionState>());
        device.Setup(item => item.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        device.Setup(item => item.ReadCharacteristic(
                StockFirmwareProfile.Service.ToString(), StockFirmwareProfile.Identity.ToString()))
            .Returns(Observable.Return(new BleCharacteristicResult(
                null!, BleCharacteristicEvent.Read,
                Encoding.UTF8.GetBytes("aa:bb:cc:dd:ee:ff;ESP32;0;0123456789abcdef0123456789abcdef;1.0.34"))));
        device.Setup(item => item.CancelConnection()).Throws(new IOException("Native disconnect failed."));
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        ble.Setup(item => item.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        ble.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        var manager = mocks.GetRequiredObject<IBleManager>();
        var identifier = new StockDeviceIdentifier("selected", "Chosen", peripheral, manager);

        var error = await Assert.ThrowsAsync<UnsettledDeviceCleanupException>(() =>
            CreateProvider(new Lazy<IBleManager>(() => manager))
                .GetDeviceAsync(identifier, TestContext.Current.CancellationToken));

        Assert.IsType<IOException>(error.InnerException);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Theory]
    [InlineData(BleCharacteristicEvent.Notification, "aa:bb:cc:dd:ee:ff;ESP32;1;0123456789abcdef0123456789abcdef;1.0.34", false)]
    [InlineData(BleCharacteristicEvent.Read, "", false)]
    [InlineData(BleCharacteristicEvent.Read, "malformed", false)]
    [InlineData(BleCharacteristicEvent.Read, "ignored", true)]
    public async Task InvalidIdentityEventOrPayloadDisconnects(
        BleCharacteristicEvent eventType, string content, bool oversized)
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var device = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        device.SetupGet(item => item.Uuid).Returns("selected");
        device.SetupGet(item => item.Status).Returns(ConnectionState.Connected);
        device.Setup(item => item.WhenStatusChanged())
            .Returns(Observable.Return(ConnectionState.Disconnected));
        device.Setup(item => item.WhenConnectionFailed()).Returns(Observable.Never<BleException>());
        device.Setup(item => item.ReadCharacteristic(
                StockFirmwareProfile.Service.ToString(), StockFirmwareProfile.Identity.ToString()))
            .Returns(Observable.Return(new BleCharacteristicResult(
                null!, eventType, oversized ? new byte[257] : Encoding.UTF8.GetBytes(content))));
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        ble.Setup(item => item.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        ble.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        var manager = mocks.GetRequiredObject<IBleManager>();
        var identifier = new StockDeviceIdentifier("selected", "Chosen", peripheral, manager);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateProvider(new Lazy<IBleManager>(() => manager))
                .GetDeviceAsync(identifier, TestContext.Current.CancellationToken));
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task CanceledIdentityReadDisconnectsWithoutReportingTimeout()
    {
        using var mocks = new Mocker();
        using var cancellation = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, _, identifier) = ArrangePendingRead(mocks, reading);
        var read = CreateProvider(new Lazy<IBleManager>(() => manager))
            .GetDeviceAsync(identifier, cancellation.Token);
        await reading.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task FailedIdentityReadPropagatesFaultAndStillDisconnects()
    {
        using var mocks = new Mocker();
        var (manager, _, identifier) = ArrangePendingRead(
            mocks, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        mocks.GetOrCreateMock<IPeripheral>()
            .AsMoq()
            .Setup(item => item.ReadCharacteristic(
                StockFirmwareProfile.Service.ToString(), StockFirmwareProfile.Identity.ToString()))
            .Returns(Observable.Throw<BleCharacteristicResult>(new IOException("Native read failed.")));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CreateProvider(new Lazy<IBleManager>(() => manager))
                .GetDeviceAsync(identifier, TestContext.Current.CancellationToken));

        Assert.Contains("Native read failed", error.Message, StringComparison.Ordinal);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task IdentityReadDeadlineThrowsTimeoutAndStillDisconnects()
    {
        using var mocks = new Mocker();
        var clock = new FakeTimeProvider();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, _, identifier) = ArrangePendingRead(mocks, reading);
        var read = CreateProvider(new Lazy<IBleManager>(() => manager), clock)
            .GetDeviceAsync(identifier, TestContext.Current.CancellationToken);
        await reading.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<TimeoutException>(() => read);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task DisconnectDeadlineReportsUnsettledCleanupWithoutSleep()
    {
        using var mocks = new Mocker();
        var clock = new FakeTimeProvider();
        var disconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var device = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        device.SetupGet(item => item.Uuid).Returns("selected");
        device.SetupGet(item => item.Status).Returns(ConnectionState.Connected);
        device.Setup(item => item.WhenStatusChanged()).Returns(Observable.Never<ConnectionState>());
        device.Setup(item => item.WhenConnectionFailed()).Returns(Observable.Never<BleException>());
        device.Setup(item => item.CancelConnection()).Callback(() => disconnectStarted.TrySetResult());
        device.Setup(item => item.ReadCharacteristic(
                StockFirmwareProfile.Service.ToString(), StockFirmwareProfile.Identity.ToString()))
            .Returns(Observable.Return(new BleCharacteristicResult(
                null!, BleCharacteristicEvent.Read,
                Encoding.UTF8.GetBytes("aa:bb:cc:dd:ee:ff;ESP32;0;0123456789abcdef0123456789abcdef;1.0.34"))));
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        ble.Setup(item => item.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        ble.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        var manager = mocks.GetRequiredObject<IBleManager>();
        var identifier = new StockDeviceIdentifier("selected", "Chosen", peripheral, manager);
        var read = CreateProvider(new Lazy<IBleManager>(() => manager), clock)
            .GetDeviceAsync(identifier, TestContext.Current.CancellationToken);
        await disconnectStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<UnsettledDeviceCleanupException>(() => read);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task DisposedManagerRefusalDoesNotConnectOrRead()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(item => item.RequestAccess())
            .Returns(Observable.Throw<AccessState>(new ObjectDisposedException("BLE manager")));
        var device = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        device.SetupGet(item => item.Uuid).Returns("selected");
        var manager = mocks.GetRequiredObject<IBleManager>();
        var identifier = new StockDeviceIdentifier(
            "selected", "Chosen", mocks.GetRequiredObject<IPeripheral>(), manager);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            CreateProvider(new Lazy<IBleManager>(() => manager))
                .GetDeviceAsync(identifier, TestContext.Current.CancellationToken));
        mocks.Verify<IBleManager>(item => item.GetKnownPeripheral("selected"), TimesSpec.NeverCalled);
        mocks.Verify<IPeripheral>(item => item.Connect(FastArg.Any<ConnectionConfig>()),
            TimesSpec.NeverCalled);
    }

    private static StockDeviceProvider CreateProvider(Lazy<IBleManager> manager, TimeProvider? clock = null) =>
        new(manager, clock ?? TimeProvider.System, DefaultScheduler.Instance);

    private static (IBleManager Manager, IPeripheral Peripheral, StockDeviceIdentifier Identifier)
        ArrangePendingRead(Mocker mocks, TaskCompletionSource reading)
    {
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        var device = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        device.SetupGet(item => item.Uuid).Returns("selected");
        device.SetupGet(item => item.Status).Returns(ConnectionState.Connected);
        device.Setup(item => item.WhenStatusChanged())
            .Returns(Observable.Return(ConnectionState.Disconnected));
        device.Setup(item => item.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        device.Setup(item => item.ReadCharacteristic(
                StockFirmwareProfile.Service.ToString(), StockFirmwareProfile.Identity.ToString()))
            .Returns(Observable.Create<BleCharacteristicResult>(_ =>
            {
                reading.TrySetResult();
                return System.Reactive.Disposables.Disposable.Empty;
            }));
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        ble.Setup(item => item.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        ble.Setup(item => item.GetKnownPeripheral("selected")).Returns(peripheral);
        var manager = mocks.GetRequiredObject<IBleManager>();
        return (manager, peripheral, new StockDeviceIdentifier("selected", "Chosen", peripheral, manager));
    }
}
#endif
