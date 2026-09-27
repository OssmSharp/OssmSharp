#if WINDOWS || MACOS
namespace OssmSharp.Cli.IntegrationTests;

internal sealed class GatedPromptReader(
    IReadOnlyList<string> lines,
    int gatedLineIndex,
    Task gate) : TextReader
{
    private int _index;

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_index == gatedLineIndex)
            await gate.WaitAsync(cancellationToken);

        return _index < lines.Count ? lines[_index++] : null;
    }
}
#endif
