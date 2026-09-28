namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Owns serialized firmware-setting submission and a device connection, not motor behavior or safety.
/// </summary>
public interface IOssmDevice : IAsyncDisposable
{
    /// <summary>
    /// Notifies subscribers when last known state or command-arming status changes.
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Gets whether settings submission is enabled by an explicit caller reset.
    /// </summary>
    bool IsArmed { get; }

    /// <summary>
    /// Gets the terminal provider failure, or null when no such failure has been observed.
    /// </summary>
    /// <remarks>
    /// A failure raises <see cref="StateChanged"/> and requires disposal and a new connection instance.
    /// A null value does not establish that a connection has been opened.
    /// </remarks>
    Exception? ConnectionFailure { get; }

    /// <summary>
    /// Connects, subscribes to observations, and reads initial state without arming or sending settings.
    /// </summary>
    /// <returns>Completion of connection and initial observation; provider errors propagate.</returns>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits complete settings, replacing older unissued commands.
    /// </summary>
    /// <param name="settings">The complete firmware pattern and control arguments.</param>
    /// <param name="cancellationToken" />
    /// <returns>The outcome and count of successfully completed writes.</returns>
    /// <remarks>
    /// Cancellation discards unissued commands and returns <see cref="SettingsOutcome.Cancelled"/>.
    /// Issued I/O is awaited, even after supersession, Stop, or cancellation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is not connected or is disarmed.</exception>
    /// <exception cref="DeviceWriteException">A write failed; the exception retains known partial progress.</exception>
    Task<SettingsResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns last known state or performs a serialized live read without masking read failures.
    /// </summary>
    /// <param name="bypassCache">Whether to read from the provider instead of returning last known state.</param>
    /// <param name="cancellationToken" />
    /// <returns>The last observation, or null when no observation has been received.</returns>
    Task<DeviceState?> GetStateAsync(bool bypassCache = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disarms immediately, discards pending settings, and sends a speed-zero primitive after issued I/O settles.
    /// </summary>
    /// <returns>Completion of the Stop write; failures propagate and leave submission disarmed.</returns>
    /// <remarks>
    /// Cancellation can prevent an unissued Stop write. It never rearms the device or recalls issued I/O.
    /// </remarks>
    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables submission after preceding I/O settles, without sending or restoring motion settings.
    /// </summary>
    /// <returns>Completion of the explicit reset.</returns>
    /// <exception cref="InvalidOperationException">Not connected, or a concurrent Stop or external change invalidated the reset.</exception>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
