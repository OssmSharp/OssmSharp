namespace OssmSharp.Device.Stock.Tests;

internal sealed record SelectedStockTestIdentifier(string SelectedId) : DeviceIdentifier
{
    public override string Id => SelectedId;

    public override string Name => "Selected stock device";
}
