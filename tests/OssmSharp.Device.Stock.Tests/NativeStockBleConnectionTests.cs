#if WINDOWS || MACOS
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FastMoq.Providers.MoqProvider;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class NativeStockBleConnectionTests
{
    [Fact]
    public async Task DisposeWaitsForNativeDisconnection()
    {
        using var mocks = new Mocker();
        using var statuses = new Subject<ConnectionState>();
        var mock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        mock.SetupGet(peripheral => peripheral.Status).Returns(ConnectionState.Connected);
        mock.Setup(peripheral => peripheral.WhenStatusChanged()).Returns(statuses);
        mock.Setup(peripheral => peripheral.WhenConnectionFailed()).Returns(Observable.Never<BleException>());
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        var connection = new NativeStockBleConnection(peripheral, StockFirmwareProfile.Service);

        var disposal = connection.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        statuses.OnNext(ConnectionState.Disconnected);
        await disposal.WaitAsync(TestContext.Current.CancellationToken);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }

    [Fact]
    public async Task DisposeWithoutNativeDisconnectEventFailsAtDeadline()
    {
        using var mocks = new Mocker();
        using var statuses = new Subject<ConnectionState>();
        var clock = new FakeTimeProvider();
        var mock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        mock.SetupGet(peripheral => peripheral.Status).Returns(ConnectionState.Connected);
        mock.Setup(peripheral => peripheral.WhenStatusChanged()).Returns(statuses);
        mock.Setup(peripheral => peripheral.WhenConnectionFailed()).Returns(Observable.Never<BleException>());
        var connection = new NativeStockBleConnection(
            mocks.GetRequiredObject<IPeripheral>(), StockFirmwareProfile.Service, clock);

        var cleanup = connection.DisposeAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => cleanup.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Contains("did not settle", error.Message, StringComparison.Ordinal);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }
}
#endif
