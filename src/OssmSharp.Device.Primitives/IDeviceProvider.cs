using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Device.Primitives;

/// <summary>
/// Discovers device selections and reads their descriptive identity without opening motion sessions.
/// </summary>
public interface IDeviceProvider
{
    /// <summary>
    /// Scans for currently advertised devices without connecting.
    /// </summary>
    /// <param name="cancellationToken">Cancels discovery and releases its subscriptions.</param>
    /// <returns>Composition-bound selections that must be passed unchanged to this provider.</returns>
    Task<IReadOnlyList<DeviceIdentifier>> GetAvailableDevicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads identity without opening a motion device session.
    /// </summary>
    /// <param name="device">The original selection returned by this provider.</param>
    /// <param name="cancellationToken">Cancels retrieval while preserving owned connection cleanup.</param>
    /// <returns>Identity information with the original identifier, no verified profile and no supported patterns.</returns>
    /// <exception cref="ArgumentException">The identifier does not belong to this provider.</exception>
    Task<DeviceInformation> GetDeviceAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken);
}
