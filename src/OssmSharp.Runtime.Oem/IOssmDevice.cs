namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Owns serialized firmware-setting submission and a reusable device connection, not motor behavior or safety.
/// </summary>
/// <remarks>
/// An undisposed client can reconnect after an explicit disconnect, connection loss, or failed setup.
/// Each connection starts with fresh state and never replays earlier settings.
/// </remarks>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Notifies subscribers when last known state or command-arming status changes.
    /// </summary>
    /// <remarks>
    /// Subscriptions survive <see cref="DisconnectAsync"/> and reconnection, and are released by disposal.
    /// </remarks>
    event EventHandler? StateChanged;

    /// <summary>
    /// Gets whether settings submission is armed by successful connection or explicit reset.
    /// </summary>
    /// <remarks>
    /// A successful Stop preserves the current arming state. Stop failure, operation failure, disconnection,
    /// connection failure, and disposal disarm submission.
    /// Valid control observations update last known state without arming, disarming, or cancelling settings.
    /// </remarks>
    bool IsArmed { get; }

    /// <summary>
    /// Gets the most recent connection failure, or null when none has been observed since the last successful connection.
    /// </summary>
    /// <remarks>
    /// A failure raises <see cref="StateChanged"/>, disarms, and ends the connection. Only a successful
    /// <see cref="ConnectAsync"/> clears it; <see cref="ResetAsync"/> does not.
    /// A null value does not establish that a connection is open.
    /// </remarks>
    Exception? ConnectionFailure { get; }

    /// <summary>
    /// Connects or reconnects, establishes fresh observations and initial state, and arms submission without sending settings.
    /// </summary>
    /// <returns>Completion of connection and initial observation; completes immediately when already connected.</returns>
    /// <remarks>
    /// Cleanup of an earlier connection finishes first; its failure propagates without opening another connection.
    /// A failed, cancelled, or interrupted attempt releases its partial connection and remains disarmed.
    /// Stop, disconnect, and disposal interrupt unfinished setup without sending motion settings.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disarms, discards settings not yet sent, sends or joins Stop when connected, and releases connection resources.
    /// </summary>
    /// <returns>Completion of Stop and cleanup; failures propagate after cleanup, which can be retried on reconnection.</returns>
    /// <remarks>
    /// The client, last known state, and <see cref="StateChanged"/> subscriptions are preserved for reconnection.
    /// In-progress live reads are abandoned; issued transfers settle before their connection resources are released.
    /// Cancellation before the call starts prevents disconnect; once started, cleanup completes regardless of cancellation.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the device to run patterns, then submits complete settings, replacing older commands not yet sent.
    /// </summary>
    /// <param name="settings">The complete firmware pattern and control arguments.</param>
    /// <param name="cancellationToken" />
    /// <returns>The result and count of writes confirmed by terminal responses.</returns>
    /// <remarks>
    /// Entering the pattern mode can reset the device's controls before the settings are written; that reset does not disarm.
    /// Cancellation discards commands not yet sent and returns <see cref="SetStatus.Cancelled"/>.
    /// Supersession, Stop, and cancellation abandon earlier response waits after issued transfers settle;
    /// those assignments may still take effect even when they are not counted as confirmed writes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is not connected, is disarmed, Stop is in progress, or the device is not ready to run patterns; no settings were sent.</exception>
    /// <exception cref="DeviceWriteException">A write failed; the exception retains known partial progress.</exception>
    /// <exception cref="IOException">Asking the device to run patterns failed; no settings were sent and submission is disarmed.</exception>
    Task<SetResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns last known state or performs a serialized live read without masking read failures.
    /// </summary>
    /// <param name="bypassCache">Whether to read from the provider instead of returning last known state.</param>
    /// <param name="cancellationToken" />
    /// <returns>The last observation, or null when no observation has been received.</returns>
    /// <remarks>
    /// Cached reads remain available while disconnected. Live reads require a connection and survive Stop,
    /// but disconnect and disposal abandon them with an exception rather than returning cached success.
    /// </remarks>
    Task<DeviceState?> GetStateAsync(bool bypassCache = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards settings not yet sent and sends speed zero ahead of queued requests without changing arming on success.
    /// </summary>
    /// <returns>Completion of the Stop write; failures propagate and leave submission disarmed.</returns>
    /// <remarks>
    /// Stop waits for an issued GATT transfer, not an earlier terminal response or live read.
    /// A command sent just before Stop may still briefly take effect; Stop completion does not confirm physical motion.
    /// During connection setup, Stop ends the attempt without sending a command. Concurrent callers share an in-progress Stop.
    /// Stop does not require arming. Success preserves the current arming state; failure disarms submission.
    /// Cancellation is checked before starting Stop; once started, its write runs to completion.
    /// </remarks>
    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms submission after preceding I/O settles, without sending or restoring motion settings.
    /// </summary>
    /// <returns>Completion of the explicit reset.</returns>
    /// <remarks>
    /// Reset does not clear <see cref="ConnectionFailure"/> and cannot rearm during Stop.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected, or a concurrent Stop or operation failure invalidated the reset.</exception>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
