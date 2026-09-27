#if WINDOWS || MACOS
using FastMoq.Providers.MoqProvider;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock.Tests;

public sealed class StockDeviceIdentifierTests
{
    [Fact]
    public void IdentifierSnapshotsPublicMetadataAndRejectsForeignComposition()
    {
        using var mocks = new Mocker();
        var manager = mocks.GetRequiredObject<IBleManager>();
        var otherManager = mocks.CreateStandaloneFastMock<IBleManager>().Instance;
        var peripheralMock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        peripheralMock.SetupGet(peripheral => peripheral.Uuid).Returns("selected-id");
        peripheralMock.SetupGet(peripheral => peripheral.Name).Returns("Changed");
        var peripheral = mocks.GetRequiredObject<IPeripheral>();
        var identifier = new StockDeviceIdentifier("selected-id", "Original", peripheral, manager);

        Assert.Equal("selected-id", identifier.Id);
        Assert.Equal("Original", identifier.Name);
        Assert.Same(peripheral, StockDeviceIdentifier.Validate(identifier, manager));
        Assert.Throws<ArgumentException>(() => StockDeviceIdentifier.Validate(identifier, otherManager));
    }

    [Fact]
    public void IdentifierRejectsChangedPeripheralAndUnrelatedIdentifier()
    {
        using var mocks = new Mocker();
        var manager = mocks.GetRequiredObject<IBleManager>();
        var peripheralMock = mocks.GetOrCreateMock<IPeripheral>().AsMoq();
        peripheralMock.SetupGet(peripheral => peripheral.Uuid).Returns("different-id");
        var identifier = new StockDeviceIdentifier(
            "selected-id", "Device", mocks.GetRequiredObject<IPeripheral>(), manager);

        Assert.Throws<ArgumentException>(() => StockDeviceIdentifier.Validate(identifier, manager));
        Assert.Throws<ArgumentException>(() =>
            StockDeviceIdentifier.Validate(new ForeignDeviceIdentifier(), manager));
    }
}
#endif
