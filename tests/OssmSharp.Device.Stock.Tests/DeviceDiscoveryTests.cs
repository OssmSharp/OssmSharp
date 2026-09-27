#if WINDOWS || MACOS
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using FastMoq.Providers.MoqProvider;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class DeviceDiscoveryTests
{
    [Fact]
    public async Task CanceledScanDoesNotInitializeBluetooth()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var discovery = new DeviceDiscovery(new Lazy<IBleManager>(() =>
            throw new InvalidOperationException("Canceled scans must not initialize Bluetooth.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            discovery.ScanAsync(cancellation.Token));
    }

    [Fact]
    public async Task ScanFiltersServiceAndBlankIdThenDeduplicatesInDiscoveryOrder()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        var first = Peripheral(mocks, "A", "Fallback");
        var duplicate = Peripheral(mocks, "a", "Duplicate");
        var absent = Peripheral(mocks, "", "Blank");
        var other = Peripheral(mocks, "B", "No Service");
        var valid = Advertisement(mocks, StockFirmwareProfile.Service.ToString(), "Advertised");
        var missing = Advertisement(mocks, "00000000-0000-0000-0000-000000000000", "Unrelated");
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(new[]
            {
                new ScanResult(first, -30, valid),
                new ScanResult(duplicate, -35, valid),
                new ScanResult(absent, -40, valid),
                new ScanResult(other, -45, missing),
            }.ToObservable());

        var results = await new DeviceDiscovery(new Lazy<IBleManager>(
                () => mocks.GetRequiredObject<IBleManager>()))
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("A", results[0].Id);
        Assert.Equal("Advertised", results[0].Name);
        Assert.Same(first, StockDeviceIdentifier.Validate(
            results[0], mocks.GetRequiredObject<IBleManager>()));
    }

    [Theory]
    [InlineData("enrolled", true, true)]
    [InlineData("ENROLLED", true, true)]
    [InlineData("another-device", true, false)]
    [InlineData("enrolled", false, false)]
    public async Task NativeScanSelectsOnlyExactLocatorAdvertisingService(
        string seenLocator, bool advertisesService, bool expected)
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        var peripheral = Peripheral(mocks, seenLocator, "Advertised");
        var service = advertisesService
            ? StockFirmwareProfile.Service.ToString()
            : "00000000-0000-0000-0000-000000000000";
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(Observable.Return(new ScanResult(
                peripheral, -30, Advertisement(mocks, service, "Advertised"))));

        var discovered = await new DeviceDiscovery(new Lazy<IBleManager>(
                () => mocks.GetRequiredObject<IBleManager>()))
            .ScanAsync(TestContext.Current.CancellationToken);
        var selected = discovered.FirstOrDefault(device =>
            string.Equals(device.Id, "enrolled", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, selected is not null);
    }

    [Fact]
    public async Task ScanCompletesAtFiveSecondsWithoutWaitingForAnOpenSource()
    {
        using var mocks = new Mocker();
        var clock = new HistoricalScheduler();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        var disposed = false;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(Observable.Create<ScanResult>(_ =>
            {
                subscribed.TrySetResult();
                return Disposable.Create(() => disposed = true);
            }));
        var scan = new DeviceDiscovery(new Lazy<IBleManager>(
            () => mocks.GetRequiredObject<IBleManager>()), clock)
            .ScanAsync(TestContext.Current.CancellationToken);
        await subscribed.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.AdvanceBy(TimeSpan.FromSeconds(4));
        Assert.False(scan.IsCompleted);
        clock.AdvanceBy(TimeSpan.FromSeconds(1));

        Assert.Empty(await scan.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(disposed);
    }

    [Fact]
    public async Task CompletedSourceReturnsEarlyWithoutWaitingForScanDeadline()
    {
        using var mocks = new Mocker();
        var clock = new HistoricalScheduler();
        var start = clock.Now;
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(Observable.Empty<ScanResult>());

        var scan = new DeviceDiscovery(new Lazy<IBleManager>(
            () => mocks.GetRequiredObject<IBleManager>()), clock)
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await scan.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(start, clock.Now);
    }

    [Fact]
    public async Task ScanReturnsAtMost64DevicesAndUnsubscribes()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        var advertised = Advertisement(mocks, StockFirmwareProfile.Service.ToString(), "Valid");
        var disposed = false;
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(Observable.Range(0, 100)
                .Select(index => new ScanResult(
                    Peripheral(mocks, $"device-{index}", "Name"), -30, advertised))
                .Finally(() => disposed = true));

        var results = await new DeviceDiscovery(new Lazy<IBleManager>(
                () => mocks.GetRequiredObject<IBleManager>()))
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(64, results.Count);
        Assert.Equal("device-0", results[0].Id);
        Assert.Equal("device-63", results[63].Id);
        Assert.True(disposed);
    }

    [Fact]
    public async Task DeniedPermissionDoesNotStartScan()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Denied));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new DeviceDiscovery(new Lazy<IBleManager>(
                    () => mocks.GetRequiredObject<IBleManager>()))
                .ScanAsync(TestContext.Current.CancellationToken));
        mocks.Verify<IBleManager>(manager => manager.Scan(FastArg.Any<ScanConfig>()),
            TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task ScanCancellationUnsubscribesEvenWhenSourceNeverCompletes()
    {
        using var mocks = new Mocker();
        using var cancellation = new CancellationTokenSource();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(Observable.Create<ScanResult>(_ =>
            {
                subscribed.SetResult();
                return Disposable.Create(() => disposed = true);
            }));
        var scan = new DeviceDiscovery(new Lazy<IBleManager>(
                () => mocks.GetRequiredObject<IBleManager>()))
            .ScanAsync(cancellation.Token);
        await subscribed.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.True(disposed);
    }

    [Fact]
    public async Task ScanErrorUnsubscribesAndPropagatesFailure()
    {
        using var mocks = new Mocker();
        var ble = mocks.GetOrCreateMock<IBleManager>().AsMoq();
        ble.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        var disposed = false;
        ble.Setup(manager => manager.Scan(Moq.It.IsAny<ScanConfig>()))
            .Returns(Observable.Throw<ScanResult>(new IOException("Native scan failed."))
                .Finally(() => disposed = true));

        await Assert.ThrowsAsync<IOException>(() =>
            new DeviceDiscovery(new Lazy<IBleManager>(
                    () => mocks.GetRequiredObject<IBleManager>()))
                .ScanAsync(TestContext.Current.CancellationToken));
        Assert.True(disposed);
    }

    private static IPeripheral Peripheral(Mocker mocks, string id, string name)
    {
        var peripheral = mocks.CreateStandaloneFastMock<IPeripheral>();
        var moq = peripheral.AsMoq();
        moq.SetupGet(item => item.Uuid).Returns(id);
        moq.SetupGet(item => item.Name).Returns(name);
        return peripheral.Instance;
    }

    private static IAdvertisementData Advertisement(Mocker mocks, string service, string name)
    {
        var advertisement = mocks.CreateStandaloneFastMock<IAdvertisementData>();
        var moq = advertisement.AsMoq();
        moq.SetupGet(item => item.ServiceUuids).Returns([service]);
        moq.SetupGet(item => item.LocalName).Returns(name);
        return advertisement.Instance;
    }
}
#endif
