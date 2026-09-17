namespace OSSMDotNet.Models;

/// <summary>Describes the guest-safe controller status published by ControlService.</summary>
public sealed record GuestStatus(
    string Mode, string Reason, long Generation, long LockRevision, bool BridgeReady, bool DeviceMoving,
    ControlValues Desired, double? Position, double? Velocity, GuestLimits Limits, long PolicyVersion,
    string SessionMode, string? ConnectionId, long AckSeq, GuestGrant? Grant)
{
    public ControlValues? Current { get; init; }
    public long VerifiedSeq { get; init; }
    public bool DeviceEnabled { get; init; }
    public bool IsSimulator { get; init; }
    public long SnapshotRevision { get; init; }
}
