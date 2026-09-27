namespace OssmSharp.Device.Runtime;

/// <summary>Tracks only the current owner transition's expected reported tuple.</summary>
internal sealed record PendingMotionFeedback(
    MotionSettings Settings,
    long Generation,
    long IssuedAfterReportSequence,
    TaskCompletionSource Completion);
