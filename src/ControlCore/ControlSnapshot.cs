using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Web.ControlCore;

public sealed record ControlSnapshot(
    string Mode,
    string Reason,
    long Generation,
    long LockRevision,
    bool BridgeReady,
    bool DeviceMoving,
    DesiredState Desired,
    DeviceSample? Observed,
    ControlLimits Limits,
    long PolicyVersion,
    string SessionMode,
    string? SessionId,
    string? ConnectionId,
    long AckSeq,
    MotionGrant? Grant)
{
    public bool OperationPending { get; init; }
    public double? Position { get; init; }
    public double? Velocity { get; init; }
    public DesiredState? Current { get; init; }
    public long VerifiedSeq { get; init; }
    public bool DeviceEnabled { get; init; }
    public bool IsSimulator { get; init; }
}
