#if WINDOWS
using System.Reactive.Subjects;
using FastMoq.Provider.Moq;
using Moq;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class WindowsStockBleConnectionTests
{
    [Fact]
    public async Task DisposeWaitsForNativeDisconnection()
    {
        using var mocks = new Mocker();
        using var statuses = new Subject<ConnectionState>();
        mocks.GetOrCreateMock<IPeripheral>().AsMoq().SetupGet(peripheral => peripheral.Status)
            .Returns(ConnectionState.Connected);
        mocks.AddMethodResult<IPeripheral, IObservable<ConnectionState>>(
            peripheral => peripheral.WhenStatusChanged(), statuses);
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        var connection = new WindowsStockBleConnection(peripheral, StockFirmwareProfile.Service);

        var disposal = connection.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        statuses.OnNext(ConnectionState.Disconnected);
        await disposal.WaitAsync(TestContext.Current.CancellationToken);
        mocks.Verify<IPeripheral>(item => item.CancelConnection(), TimesSpec.Once);
    }
}
#endif
