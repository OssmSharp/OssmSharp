namespace OssmSharp.Device.Runtime;

internal sealed record PendingMotionRequest(
    MotionSettings Settings,
    long AuthorityRevision,
    Func<MotionSettings, bool> Guard,
    CancellationTokenSource Revocation);
