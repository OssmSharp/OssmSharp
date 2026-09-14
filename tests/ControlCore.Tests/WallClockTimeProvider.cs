using Microsoft.Extensions.Time.Testing;

namespace OSSMDotNet.Web.ControlCore.Tests;

internal sealed class WallClockTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Offset;
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
}
