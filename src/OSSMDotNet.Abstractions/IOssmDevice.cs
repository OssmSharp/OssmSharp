using OneOf;

namespace OSSMDotNet.Abstractions;

/// <summary>
/// Provides state updates and motion control for an OSSM device implementation.
/// </summary>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Occurs after <see cref="ConnectionState"/> changes; a transition does not indicate fresh motion telemetry.
    /// </summary>
    event Action<DeviceConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Occurs when the device reports state or an asynchronous device fault is detected.
    /// </summary>
    /// <remarks>Connection status alone is reported through <see cref="ConnectionStateChanged"/>, not this event.</remarks>
    event Action<OneOf<DeviceState, Exception>>? DeviceStateChanged;

    /// <summary>
    /// Gets the current connection status independently of device state updates.
    /// </summary>
    /// <remarks>Reading this property does not start a connection or authorize motion.</remarks>
    DeviceConnectionState ConnectionState { get; }

    /// <summary>
    /// Gets the device information discovered by the implementation and retained for the instance lifetime.
    /// </summary>
    /// <remarks>
    /// Metadata is not supplied by callers or constructor arguments. Reading this property has no side effects:
    /// it does not start discovery, fabricate metadata or authorize motion.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Device information has not yet been discovered.</exception>
    DeviceInformation Device { get; }

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
