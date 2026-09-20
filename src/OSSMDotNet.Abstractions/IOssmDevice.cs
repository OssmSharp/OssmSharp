using OneOf;

namespace OSSMDotNet.Abstractions;

/// <summary>
/// Provides observations and motion control for an OSSM device implementation.
/// </summary>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Occurs when device facts change or an asynchronous device fault is detected.
    /// </summary>
    event Action<OneOf<DeviceSnapshot, Exception>>? DeviceChanged;

    /// <summary>
    /// Gets the current retained device facts.
    /// </summary>
    /// <returns>The current snapshot. A successful result does not imply live telemetry.</returns>
    Task<DeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends the requested motion settings to the device.
    /// </summary>
    /// <param name="motionSettings">The motion settings to write.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Completion after the write operation settles.</returns>
    Task SetMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken);
}
