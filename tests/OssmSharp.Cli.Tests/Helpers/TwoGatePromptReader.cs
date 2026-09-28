namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class TwoGatePromptReader(
    IReadOnlyList<string> lines,
    Task firstWrite,
    Task secondWrite) : TextReader
{
    private int _index;

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_index == 1)
            await firstWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_index == 2)
            await secondWrite.WaitAsync(cancellationToken).ConfigureAwait(false);

        return _index < lines.Count ? lines[_index++] : null;
    }
}
