using OssmSharp.Runtime.Oem.Protocol;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Manages a device connection and processes settings requests one at a time.
/// </summary>
/// <remarks>
/// The firmware controls motion and decides whether it is safe.
/// The client can reconnect after a disconnect or connection failure unless it has been disposed.
/// Each new connection reads fresh state and does not resend earlier settings.
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
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
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
    /// Cancellation before this call starts prevents disconnection. Once started, cleanup continues even if cancelled.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
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
    /// Cancellation drops commands not yet sent and returns <see cref="SetStatus.Cancelled"/>.
    /// A newer request, Stop, or cancellation ends waits for earlier device responses after transfers already started finish.
    /// Commands already sent may still take effect even if the result does not count them as confirmed writes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is disconnected, requests are disarmed, Stop is running, or the device is not ready to run patterns. No settings were sent.</exception>
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
    /// </remarks>
    Task<DeviceState?> GetStateAsync(bool bypassCache = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops settings not yet sent and sends zero raw velocity before queued requests.
    /// </summary>
    /// <returns>Completion of the Stop command. A failed command returns an error and leaves requests disarmed.</returns>
    /// <remarks>
    /// Stop waits for a device transfer already started, but not for an earlier device response or a live read.
    /// A command sent just before Stop may still briefly take effect.
    /// During connection setup, Stop ends the connection attempt without sending a command.
    /// Calls made while Stop is running wait for the same Stop command.
    /// Stop is allowed while requests are disarmed. Success leaves <see cref="IsArmed"/> unchanged; failure disarms requests.
    /// Cancellation is checked before a new Stop starts. Once started, its command finishes even if cancelled.
    /// </remarks>
    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for earlier I/O to finish, then enables requests without sending or restoring motion settings.
    /// </summary>
    /// <returns>Permission to send settings requests again.</returns>
    /// <remarks>
    /// Reset does not clear <see cref="ConnectionFailure"/> and cannot allow requests while Stop is running.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is disconnected, Stop is running, or a Stop or operation failure interrupted the reset.</exception>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
