using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Web.Contracts;

/// <summary>Describes the controller state exposed to operator and guest clients.</summary>
/// <param name="Mode">The current controller mode.</param>
/// <param name="Reason">The reason for the current mode.</param>
/// <param name="Generation">The generation used to reject stale device work.</param>
/// <param name="LockRevision">The revision of the external lock state.</param>
/// <param name="BridgeReady">Whether the device bridge is ready.</param>
/// <param name="DeviceMoving">Whether the device reports motion.</param>
/// <param name="Desired">The controller's requested motion settings.</param>
/// <param name="Observed">The latest device state, if available.</param>
/// <param name="Limits">The active motion limits.</param>
/// <param name="PolicyVersion">The version of the active motion policy.</param>
/// <param name="SessionMode">The current guest session state.</param>
/// <param name="SessionId">The active session identifier, if any.</param>
/// <param name="ConnectionId">The admitted guest connection, if any.</param>
/// <param name="AckSeq">The most recent guest input sequence acknowledged by the controller.</param>
/// <param name="Grant">The current bounded motion grant, if one exists.</param>
public sealed record ControlSnapshot(
    string Mode, string Reason, long Generation, long LockRevision, bool BridgeReady, bool DeviceMoving,
    MotionSettings Desired, DeviceState? Observed, ControlLimits Limits, long PolicyVersion, string SessionMode,
    string? SessionId, string? ConnectionId, long AckSeq, MotionGrant? Grant)
{
    /// <summary>
    /// Gets whether device work remains pending or awaits feedback.
    /// </summary>
    public bool OperationPending { get; init; }
    /// <summary>
    /// Gets the latest available reported position in millimetres.
    /// </summary>
    public double? Position { get; init; }
    /// <summary>
    /// Gets the latest available reported velocity in millimetres per second.
    /// </summary>
    public double? Velocity { get; init; }
    /// <summary>
    /// Gets the latest available reported motion settings.
    /// </summary>
    public MotionSettings? Current { get; init; }
    /// <summary>
    /// Gets the most recent guest input sequence verified by device feedback.
    /// </summary>
    public long VerifiedSeq { get; init; }
    /// <summary>
    /// Gets whether the device reports enabled motion.
    /// </summary>
    public bool DeviceEnabled { get; init; }
    /// <summary>
    /// Gets whether the active device uses the simulator profile.
    /// </summary>
    public bool IsSimulator { get; init; }
}
