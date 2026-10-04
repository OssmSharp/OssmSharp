using OssmSharp.Runtime.Oem.Protocol;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Manages a device connection and processes settings requests one at a time.
/// </summary>
/// <remarks>
/// The firmware controls motion and decides whether it is safe.
/// The client can reconnect after a disconnect or connection failure unless it has been disposed.
/// Each new connection reads fresh state and does not resend earlier settings.
/// Disposal cancels calls still waiting for serialized I/O. Cancelled queued Connect, Disconnect, fresh-state,
/// and Reset calls throw <see cref="OperationCanceledException"/>; settings requests use <see cref="SetResult"/> as described below.
/// New device-operation calls after disposal begins throw <see cref="ObjectDisposedException"/>;
/// repeated disposal calls observe the same cleanup result.
/// </remarks>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Notifies subscribers when the last known state or permission to send settings changes.
    /// </summary>
    /// <remarks>
    /// Subscriptions remain active after disconnection and reconnection. Disposing the client removes them.
    /// </remarks>
    event EventHandler? StateChanged;

    /// <summary>
    /// Gets whether the device connection is ready and still open.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the device's unique identifier.
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// Gets the selected device's name, or null when it is unavailable.
    /// </summary>
    /// <remarks>
    /// The name is unavailable before a device is selected, after connection cleanup,
    /// or when the Bluetooth provider supplies no name.
    /// </remarks>
    string? Name { get; }

    /// <summary>
    /// Gets whether the client allows settings requests.
    /// </summary>
    /// <remarks>
    /// A successful connection or <see cref="ResetAsync"/> allows requests. A successful Stop leaves this value unchanged.
    /// A failed Stop, failed operation, disconnection, failed connection, or disposal disarms requests.
    /// Receiving valid device state updates the last known state but does not allow, disarm, or cancel requests.
    /// </remarks>
    bool IsArmed { get; }

    /// <summary>
    /// Gets the latest connection error, or null when none has occurred since the last successful connection.
    /// </summary>
    /// <remarks>
    /// A connection error raises <see cref="StateChanged"/>, disarms requests, and ends the connection.
    /// A successful <see cref="ConnectAsync"/> clears the error; <see cref="ResetAsync"/> does not.
    /// Use <see cref="IsConnected"/> to check the connection, even when this value is null.
    /// </remarks>
    Exception? ConnectionFailure { get; }

    /// <summary>
    /// Connects to the device, reads its initial state, and allows requests without sending settings.
    /// </summary>
    /// <returns>An open connection with initial device state. Does nothing if already connected.</returns>
    /// <remarks>
    /// Cleanup of an earlier connection must finish first. If cleanup fails, the error is returned and no new connection opens.
    /// Failed, cancelled, or interrupted connection attempts release their resources and leave requests disarmed.
    /// Stop, disconnect, and disposal interrupt connection setup without sending motion settings.
    /// Caller cancellation applies to the I/O gate wait and setup waits, but does not cancel issued GATT transfers
    /// or required cleanup. Partial-connection cleanup is attempted before the call returns; a cleanup failure
    /// is propagated, together with the setup error when both fail.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation or disposal cancelled a wait or setup step.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disarms requests, drops settings not yet sent, stops the connected device, and closes the connection.
    /// </summary>
    /// <returns>Completion of Stop and connection cleanup. Any error is returned after cleanup is attempted.</returns>
    /// <remarks>
    /// If Stop is already running, this call waits for it instead of sending another Stop.
    /// The client keeps its last known state and <see cref="StateChanged"/> subscriptions and can reconnect later.
    /// Live reads are interrupted. Device transfers already started must finish before their connection resources are released.
    /// Failed cleanup can be retried when reconnecting.
    /// Caller cancellation is checked before accepting disconnection. Once accepted, Stop and cleanup ignore that token.
    /// Disposal can still cancel this call's I/O gate wait and take ownership of cleanup.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The caller token was cancelled before acceptance, or disposal cancelled the I/O gate wait.</exception>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the device to run patterns, then sends the requested settings, replacing older commands not yet sent.
    /// </summary>
    /// <param name="settings">The requested pattern, speed, stroke, depth, and sensation. Speed is an integer percentage from zero through 100.</param>
    /// <param name="cancellationToken" />
    /// <returns>The result, including why the request ended and how many writes the device confirmed.</returns>
    /// <remarks>
    /// Entering pattern mode can reset the device's controls before the settings are sent. This does not disarm requests.
    /// Completion does not confirm physical motion.
    /// Caller cancellation drops commands not yet sent and ends response waits for an accepted request.
    /// A newer accepted request marks its predecessor <see cref="SetStatus.Superseded"/>; Stop, disarming,
    /// disconnection, or disposal marks the current request <see cref="SetStatus.Cancelled"/>.
    /// Caller cancellation preserves a superseded result; otherwise cancellation returns <see cref="SetStatus.Cancelled"/>.
    /// These results do not replace connection, preparation, or write errors when those failures are propagated.
    /// A newer request, Stop, or cancellation ends waits for earlier device responses after transfers already started finish.
    /// Commands already sent may still take effect even if the result does not count them as confirmed writes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is disconnected, requests are disarmed, Stop or Reset Position is running, or the device is not ready to run patterns. No settings were sent.</exception>
    /// <exception cref="DeviceWriteException">A write failed. The error includes the known progress of the request.</exception>
    /// <exception cref="IOException">Asking the device to run patterns failed; no settings were sent and requests are disarmed.</exception>
    Task<SetResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the last known state or reads fresh state from the device.
    /// </summary>
    /// <param name="bypassCache">Whether to read fresh state instead of returning the last known state.</param>
    /// <param name="cancellationToken" />
    /// <returns>The device state, or null when no state is available.</returns>
    /// <remarks>
    /// The last known state remains available while disconnected.
    /// Fresh reads require a connection and run one at a time with other device operations. Stop does not cancel them.
    /// Read failures, disconnection, and disposal return an error rather than silently returning the last known state.
    /// Caller cancellation is checked before both cached and fresh reads. For a fresh read, caller cancellation,
    /// disconnection, and disposal can cancel the I/O gate wait or the wait for the next valid observation.
    /// Cancelling an observation wait does not stop the motion stream; a connection or read failure can instead be propagated.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller token was cancelled on entry, or a fresh-read wait was cancelled.</exception>
    Task<DeviceState?> GetStateAsync(bool bypassCache = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops settings not yet sent and sends zero raw velocity before queued requests.
    /// </summary>
    /// <returns>Completion of the Stop command. A failed command returns an error and leaves requests disarmed.</returns>
    /// <remarks>
    /// Stop waits for a device transfer already started, but not for an earlier device response or a live read.
    /// A command sent just before Stop may still briefly take effect.
    /// During connection setup, Stop ends the connection attempt without sending a command or waiting for setup cleanup.
    /// Calls made while Stop is running wait for the same Stop command, even if the joining caller's token is cancelled.
    /// Stop is allowed while requests are disarmed. Success leaves <see cref="IsArmed"/> unchanged; failure disarms requests.
    /// Cancellation is checked before a new Stop starts. Once started, its command finishes even if cancelled.
    /// During <see cref="ResetPositionAsync"/>, Stop also leaves streaming mode to interrupt the retract.
    /// </remarks>
    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for earlier I/O to finish, then enables requests without sending or restoring motion settings.
    /// </summary>
    /// <returns>Permission to send settings requests again.</returns>
    /// <remarks>
    /// Reset does not clear <see cref="ConnectionFailure"/> and cannot allow requests while Stop or
    /// <see cref="ResetPositionAsync"/> is running.
    /// Caller cancellation applies to the I/O gate wait and is checked again before enabling requests.
    /// Once that step begins, it ignores cancellation and sends no device commands.
    /// Disposal can cancel a queued Reset; Stop, disarming, or disconnection can instead reject it as interrupted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is disconnected, Stop or Reset Position is running, or a Stop or operation failure interrupted the reset.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation or disposal cancelled Reset before its enabling step began.</exception>
    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the rail fully back, away from the person, then stops without resuming any pattern or disarming.
    /// </summary>
    /// <returns>Completion after a fresh position reading reaches the initial endpoint and required Stop cleanup finishes.</returns>
    /// <remarks>
    /// Requires a connected, armed device ready for streaming motion. Drops pending settings and rejects
    /// settings, rearming, and another Reset Position until cleanup finishes.
    /// Uses 100% stroke and depth and 20% speed. These controls replace the previous settings; they are not restored.
    /// Waits for the endpoint without an elapsed-time completion limit; Stop or cancellation can end the wait.
    /// Success, interruption, and retract failures do not independently change whether requests are armed.
    /// Stop, caller cancellation, disconnection, and disposal interrupt pending work and the retract wait.
    /// Issued transfers settle first; required zero-speed and streaming-exit cleanup ignores caller cancellation.
    /// An interrupted retract need not reach the endpoint. Cancellation throws only after cleanup settles.
    /// Failures propagate. Disconnection and failed Stop retain their existing disarming behavior.
    /// This is distinct from software rearming through <see cref="ResetAsync"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is disconnected, disarmed, not ready, or Stop or Reset Position is already running.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation, Stop, disconnection, or disposal interrupted the retract.</exception>
    /// <exception cref="InvalidDataException">A position reading is missing, malformed, or nonfinite.</exception>
    /// <exception cref="IOException">A device command or Stop failed.</exception>
    /// <exception cref="AggregateException">The retract and its Stop cleanup both failed.</exception>
    Task ResetPositionAsync(CancellationToken cancellationToken = default);
}
