namespace OSSMDotNet.ControlCore.Models;

/// <summary>
/// Describes device execution state without application session authority.
/// </summary>
/// <param name="Mode">The controller lifecycle mode.</param>
/// <param name="Reason">The latest controller transition reason.</param>
/// <param name="Generation">The execution generation used to discard stale work.</param>
/// <param name="LockRevision">The external-lock revision required for unlocking.</param>
/// <param name="BridgeReady">Whether the device bridge can accept work.</param>
/// <param name="DeviceMoving">Whether fresh feedback reports motion.</param>
/// <param name="Desired">The accepted desired device settings.</param>
/// <param name="Observed">The latest available device feedback.</param>
/// <param name="Limits">The currently applied device limits.</param>
public sealed record ControlExecutionSnapshot(
    string Mode,
    string Reason,
    long Generation,
    long LockRevision,
    bool BridgeReady,
    bool DeviceMoving,
    MotionSettings Desired,
    DeviceState? Observed,
    ControlLimits Limits)
{
    /// <summary>Gets whether device work remains queued, issued, or awaiting feedback.</summary>
    public bool OperationPending { get; init; }

    /// <summary>Gets the fresh reported position, when available.</summary>
    public double? Position { get; init; }

    /// <summary>Gets the fresh reported velocity, when available.</summary>
    public double? Velocity { get; init; }

    /// <summary>Gets the fresh reported motion settings, when available.</summary>
    public MotionSettings? Current { get; init; }

    /// <summary>Gets whether the accepted desired settings are confirmed by fresh feedback.</summary>
    public bool DesiredConfirmed { get; init; }

    /// <summary>Gets whether the device reports nonzero speed.</summary>
    public bool DeviceEnabled { get; init; }

    /// <summary>Gets whether the active device uses the simulator profile.</summary>
    public bool IsSimulator { get; init; }
}
