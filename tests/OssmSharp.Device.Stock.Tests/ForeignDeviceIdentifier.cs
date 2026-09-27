using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Device.Stock.Tests;

internal sealed record ForeignDeviceIdentifier : DeviceIdentifier
{
    public override string Id => "foreign";

    public override string Name => "Foreign";
}
