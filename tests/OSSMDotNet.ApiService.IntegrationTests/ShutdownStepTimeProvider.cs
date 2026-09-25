namespace OSSMDotNet.ApiService.IntegrationTests;

/// <summary>
/// Advances elapsed-time readings by two seconds per read while using real timers.
/// </summary>
internal sealed class ShutdownStepTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() =>
        Interlocked.Add(ref _timestamp, TimeSpan.TicksPerSecond * 2);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime, period);
}
