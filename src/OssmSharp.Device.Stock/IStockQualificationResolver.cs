using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock;

internal interface IStockQualificationResolver
{
    StockQualifiedMapping? Resolve(DeviceInformation verified, PairingIdentity identity);
}
