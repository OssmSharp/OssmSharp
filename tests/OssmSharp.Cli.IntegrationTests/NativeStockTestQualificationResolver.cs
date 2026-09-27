#if WINDOWS || MACOS
using OssmSharp.Device.Stock;
using OssmSharp.Device.Stock.Models;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.IntegrationTests;

internal sealed class NativeStockTestQualificationResolver : IStockQualificationResolver
{
    private readonly int _maximumSpeedPercent;

    internal NativeStockTestQualificationResolver(int maximumSpeedPercent) =>
        _maximumSpeedPercent = maximumSpeedPercent;

    public StockQualifiedMapping? Resolve(DeviceInformation verified, PairingIdentity identity) =>
        new(verified, identity, 100m, 0m, 100m, _maximumSpeedPercent);
}
#endif
