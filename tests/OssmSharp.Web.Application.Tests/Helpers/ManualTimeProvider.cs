namespace OssmSharp.Web.Application.Tests.Helpers;

/// <summary>
/// Advances UTC time and elapsed timestamps together while allowing an independent UTC offset.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Gets or sets the offset applied to UTC readings but not elapsed timestamps.
    /// </summary>
    internal TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => _utcNow + Offset;

    public override long GetTimestamp() => _timestamp;

    /// <summary>
    /// Advances both the base UTC time and the monotonic timestamp by the same interval.
    /// </summary>
    /// <param name="elapsed">The interval to advance.</param>
    public void Advance(TimeSpan elapsed)
    {
        _utcNow += elapsed;
        _timestamp += elapsed.Ticks;
    }
}
