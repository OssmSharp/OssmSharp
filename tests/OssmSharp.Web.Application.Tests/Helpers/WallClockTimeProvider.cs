using Microsoft.Extensions.Time.Testing;

namespace OssmSharp.Web.Application.Tests.Helpers;

/// <summary>
/// Shifts UTC readings from a fake clock without shifting its elapsed-time timestamps.
/// </summary>
/// <param name="inner">The fake clock that supplies timestamps and the base UTC reading.</param>
internal sealed class WallClockTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    /// <summary>
    /// Gets or sets the offset applied only to UTC readings.
    /// </summary>
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Offset;
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
}
