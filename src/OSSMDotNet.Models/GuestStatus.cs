namespace OSSMDotNet.Models;

/// <summary>Describes the guest-safe controller status published by ControlService.</summary>
/// <param name="Mode">The current guest-visible controller mode.</param>
/// <param name="Reason">The reason for the current mode.</param>
/// <param name="Generation">The generation used to reject stale device work.</param>
/// <param name="LockRevision">The revision of the external lock state.</param>
/// <param name="BridgeReady">Whether the device bridge is ready.</param>
/// <param name="DeviceMoving">Whether the device reports motion.</param>
/// <param name="Desired">The guest-visible requested motion settings.</param>
/// <param name="Position">The last reported position in millimetres, if available.</param>
/// <param name="Velocity">The last reported velocity in millimetres per second, if available.</param>
/// <param name="Limits">The limits visible to the guest.</param>
/// <param name="PolicyVersion">The version of the active motion policy.</param>
/// <param name="SessionMode">The current guest session state.</param>
/// <param name="ConnectionId">The guest connection, if admitted.</param>
/// <param name="AckSeq">The most recent guest input sequence acknowledged by the controller.</param>
/// <param name="Grant">The guest-visible motion grant, if available.</param>
public sealed record GuestStatus(
    string Mode, string Reason, long Generation, long LockRevision, bool BridgeReady, bool DeviceMoving,
    ControlValues Desired, double? Position, double? Velocity, GuestLimits Limits, long PolicyVersion,
    string SessionMode, string? ConnectionId, long AckSeq, GuestGrant? Grant)
{
    /// <summary>
    /// Gets the latest available guest-visible reported motion settings.
    /// </summary>
    public ControlValues? Current { get; init; }
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
    /// <summary>
    /// Gets the ordering revision of this guest status projection.
    /// </summary>
    public long SnapshotRevision { get; init; }
}
