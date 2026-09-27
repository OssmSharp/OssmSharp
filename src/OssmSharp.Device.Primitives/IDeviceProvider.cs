using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Device.Primitives;

/// <summary>Reads descriptive identity for a compatible discovered selection.</summary>
public interface IDeviceProvider
{
    /// <summary>Reads identity without opening a motion device session.</summary>
    /// <exception cref="ArgumentException">The identifier does not belong to this provider.</exception>
    Task<DeviceIdentityDetails> ReadInformationAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken);
}
