namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class SubmissionGatedPromptReader(
    IReadOnlyList<string> lines,
    int gatedLineIndex,
    Task submissionCompleted) : TextReader
{
    private int _index;

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_index == gatedLineIndex)
            await submissionCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);

        return _index < lines.Count ? lines[_index++] : null;
    }
}
