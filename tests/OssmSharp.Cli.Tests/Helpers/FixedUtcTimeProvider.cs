namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
