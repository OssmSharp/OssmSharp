using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock;

internal sealed class UnqualifiedStockMotion : IStockQualificationResolver
{
    public StockQualifiedMapping? Resolve(DeviceInformation verified, PairingIdentity identity) => null;
}
