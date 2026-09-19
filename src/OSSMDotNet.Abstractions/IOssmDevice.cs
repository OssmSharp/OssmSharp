namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents a bridge for controlling an OSSM device.
/// </summary>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Occurs when the bridge observes a device sample.
    /// </summary>
    event Action<DeviceState>? DeviceStateReceived;

    /// <summary>
    /// Sends the requested motion settings to the device.
    /// </summary>
    /// <param name="motionSettings">The motion settings to write.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Completion of the write request.</returns>
    Task SetMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken);
}
