#if WINDOWS || MACOS
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FastMoq.Providers.MoqProvider;
using OssmSharp.Device.Stock.Models;
using Moq;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class NativeStockBleConnectionTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", false)]
    [InlineData("", false)]
    public async Task KnobLimitingRequiresLiveCanonicalReadback(string response, bool accepted)
    {
        using var mocks = new Mocker();
        var mock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        mock.Setup(peripheral => peripheral.WhenStatusChanged())
            .Returns(Observable.Never<ConnectionState>());
        mock.Setup(peripheral => peripheral.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        mock.Setup(peripheral => peripheral.WriteCharacteristic(
                StockFirmwareProfile.Service.ToString(),
                StockFirmwareProfile.KnobConfiguration.ToString(),
                It.IsAny<byte[]>(),
                true))
            .Returns(Observable.Return(default(BleCharacteristicResult)!));
        mock.Setup(peripheral => peripheral.ReadCharacteristic(
                StockFirmwareProfile.Service.ToString(),
                StockFirmwareProfile.KnobConfiguration.ToString()))
            .Returns(Observable.Return(new BleCharacteristicResult(
                null!, BleCharacteristicEvent.Read, System.Text.Encoding.ASCII.GetBytes(response))));
        await using var connection = new NativeStockBleConnection(
            mocks.GetRequiredObject<IPeripheral>(), StockFirmwareProfile.Service);

        if (accepted)
            await connection.EnableKnobLimitingAsync(
                TestContext.Current.CancellationToken, TestContext.Current.CancellationToken);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                connection.EnableKnobLimitingAsync(
                    TestContext.Current.CancellationToken, TestContext.Current.CancellationToken));

        mock.Verify(peripheral => peripheral.WriteCharacteristic(
            StockFirmwareProfile.Service.ToString(),
            StockFirmwareProfile.KnobConfiguration.ToString(),
            It.Is<byte[]>(payload => System.Text.Encoding.ASCII.GetString(payload) == "true"),
            true), Times.Once);
    }

    [Fact]
    public async Task WriteCommandUsesStockCommandCharacteristicWithResponse()
    {
        using var mocks = new Mocker();
        var mock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        mock.Setup(peripheral => peripheral.WhenStatusChanged())
            .Returns(Observable.Never<ConnectionState>());
        mock.Setup(peripheral => peripheral.WhenConnectionFailed())
            .Returns(Observable.Never<BleException>());
        mock.Setup(peripheral => peripheral.WriteCharacteristic(
                StockFirmwareProfile.Service.ToString(),
                StockFirmwareProfile.Command.ToString(),
                It.IsAny<byte[]>(),
                true))
            .Returns(Observable.Return(default(BleCharacteristicResult)!));
        await using var connection = new NativeStockBleConnection(
            mocks.GetRequiredObject<IPeripheral>(), StockFirmwareProfile.Service);
        var zero = Request.ZeroSpeed();

        await connection.WriteCommandAsync(zero, TestContext.Current.CancellationToken);

        mock.Verify(peripheral => peripheral.WriteCharacteristic(
            StockFirmwareProfile.Service.ToString(),
            StockFirmwareProfile.Command.ToString(),
            It.Is<byte[]>(payload => System.Text.Encoding.UTF8.GetString(payload) == "set:speed:0"),
            true), Times.Once);
    }

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
