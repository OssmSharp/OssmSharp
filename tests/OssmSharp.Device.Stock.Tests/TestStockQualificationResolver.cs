using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock.Tests;

internal sealed class TestStockQualificationResolver(
    bool mismatched = false, int maximumSpeedPercent = 100) : IStockQualificationResolver
{
    public StockQualifiedMapping? Resolve(DeviceInformation verified, PairingIdentity identity) =>
        new(verified, mismatched ? identity with { SketchMd5 = new string('0', 32) } : identity,
            100, 0, 100, maximumSpeedPercent);
}
