namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Tracks one in-flight settings request so a later request or disarm can end it early.
/// </summary>
/// <remarks>
/// The submitting call owns and disposes <see cref="PendingCancellation"/>. Other members are
/// not thread-safe; callers synchronize access.
/// </remarks>
/// <param name="settings">The settings being written.</param>
/// <param name="pendingCancellation">The source that stops remaining writes when cancelled.</param>
internal sealed class SetOperationContext(MotionSettings settings, CancellationTokenSource pendingCancellation)
{
    /// <summary>
    /// Gets the settings being written.
    /// </summary>
    public MotionSettings Settings { get; } = settings;

    /// <summary>
    /// Gets the source that stops remaining writes when cancelled.
    /// </summary>
    /// <remarks>
    /// Cancellation prevents further writes only; commands already written may have taken effect.
    /// </remarks>
    public CancellationTokenSource PendingCancellation { get; } = pendingCancellation;

    /// <summary>
    /// Gets or sets why the request finished; <see cref="SetStatus.Completed"/> until another result is recorded.
    /// </summary>
    public SetStatus Status { get; set; } = SetStatus.Completed;

    /// <summary>
    /// Gets or sets the number of commands written successfully.
    /// </summary>
    public int CommandsWritten { get; set; }

    /// <summary>
    /// Gets a snapshot of <see cref="Status"/> and <see cref="CommandsWritten"/>.
    /// </summary>
    public SetResult Result => new(Status, CommandsWritten);
}
