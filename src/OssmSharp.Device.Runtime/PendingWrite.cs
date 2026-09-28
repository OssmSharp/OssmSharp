namespace OssmSharp.Device.Runtime;

internal sealed record PendingWrite(
    MotionSettings? Settings,
    int? PreviousRequestedSpeed,
    int? PreviousStateSpeed,
    MotionSettings? PreviousStateMotion)
{
    internal bool Issued { get; set; }

    internal bool Reported { get; set; }
}
