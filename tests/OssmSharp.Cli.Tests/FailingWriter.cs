namespace OssmSharp.Cli.Tests;

internal sealed class FailingWriter(Func<string?, bool> shouldFail) : StringWriter
{
    public override void WriteLine(string? value)
    {
        ObjectDisposedException.ThrowIf(shouldFail(value), this);
        base.WriteLine(value);
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }
}
