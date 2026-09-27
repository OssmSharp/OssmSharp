using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Device.Primitives;

/// <summary>Discovers device selections without opening device connections.</summary>
public interface IDeviceDiscovery
{
    /// <summary>Scans for currently advertised devices.</summary>
    Task<IReadOnlyList<DeviceIdentifier>> ScanAsync(CancellationToken cancellationToken);
}
