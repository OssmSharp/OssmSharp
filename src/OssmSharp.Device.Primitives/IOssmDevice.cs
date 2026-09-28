using OneOf;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Device.Primitives;

/// <summary>
/// Reports state from, and controls the motion of, one connected OSSM device.
/// </summary>
/// <remarks>
/// The device has no concept of users, sessions or locks. Commands run in order through one queue. An emergency
/// stop drains the queue and leaves the device not ready until <see cref="InitializeAsync"/> succeeds again.
/// </remarks>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Occurs after <see cref="ConnectionState"/> changes; a transition does not indicate fresh motion telemetry.
    /// </summary>
    event Action<DeviceConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Occurs when the device reports state or an asynchronous device fault is detected.
    /// </summary>
    /// <remarks>
    /// Connection status alone is reported through <see cref="ConnectionStateChanged"/>, not this event. An external
    /// motion change is reported as <see cref="UnexpectedMotionChangeException"/>.
    /// </remarks>
    event Action<OneOf<DeviceState, Exception>>? DeviceStateChanged;

    /// <summary>
    /// Gets the current connection status independently of device state updates.
    /// </summary>
    DeviceConnectionState ConnectionState { get; }

    /// <summary>
    /// Gets the device information discovered by the implementation and retained for the instance lifetime.
    /// </summary>
    /// <exception cref="InvalidOperationException">Device information has not yet been discovered.</exception>
    DeviceInformation Device { get; }

    /// <summary>
    /// Gets the last known state: the most recent validated report on the current connection, or
    /// <see langword="null"/> before the first report.
    /// </summary>
    /// <remarks>
    /// Devices may report only when a setting changes, so an old value is not stale by itself.
    /// </remarks>
    DeviceState? LatestState { get; }

    /// <summary>
    /// Gets whether the device is initialized, connected and, by its last known state, in its play-ready state, so
    /// that <see cref="SetAsync"/> can write motion.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Queues initialization: writes zero speed, performs provider preparation, and makes the device ready.
    /// </summary>
    /// <remarks>
    /// Requires the last known state to show the device stopped in its play-ready state. It never navigates, homes or
    /// starts motion. It is also how the device becomes ready again after an emergency stop.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the queued command; a write already sent still settles.</param>
    /// <exception cref="InvalidOperationException">The last known state does not show the device stopped and play-ready.</exception>
    /// <exception cref="OperationCanceledException">An emergency stop, disconnect or the caller cancelled it.</exception>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Queues the complete settings and completes when they have been written.
    /// </summary>
    /// <remarks>
    /// Commands run one at a time in order. Every call writes all of the requested settings, whatever the last known
    /// state, and completion does not wait for the device to report them. Requires <see cref="IsReady"/>; use
    /// <see cref="EmergencyStopAsync"/> to stop a device that is not ready.
    /// </remarks>
    /// <param name="settings">The complete requested settings.</param>
    /// <param name="cancellationToken">Cancels the queued command; a write already sent still settles.</param>
    /// <exception cref="InvalidOperationException">The device is not ready, or the provider does not support the settings.</exception>
    /// <exception cref="OperationCanceledException">An emergency stop, disconnect or the caller cancelled it.</exception>
    Task SetAsync(MotionSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels every queued command, lets a write already sent settle, then writes speed zero only.
    /// </summary>
    /// <remarks>
    /// The device is not ready afterwards; call <see cref="InitializeAsync"/> before moving again. The device also
    /// runs this itself on an external change, a device error, a failed write, or a report showing it left its
    /// play-ready state. Completion means the write settled, not that the device physically stopped.
    /// </remarks>
    /// <param name="cancellationToken">Cancels waiting for the Stop to settle.</param>
    /// <exception cref="IOException">The device is not connected, so no Stop could be written.</exception>
    Task EmergencyStopAsync(CancellationToken cancellationToken);
}
