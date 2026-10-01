using System.Threading.Channels;

namespace OssmSharp.Cli.Tests;

internal sealed class PromptInput : IPromptInput
{
    private readonly Channel<PromptInputResult> results = Channel.CreateUnbounded<PromptInputResult>();
    private readonly Channel<bool> reads = Channel.CreateUnbounded<bool>();

    internal PromptInput(params string?[] lines)
    {
        foreach (var line in lines)
        {
            results.Writer.TryWrite(new(line));
        }
    }

    public async Task<PromptInputResult> ReadAsync(CancellationToken cancellationToken)
    {
        reads.Writer.TryWrite(true);
        return await results.Reader.ReadAsync(cancellationToken);
    }

    internal void Send(PromptInputResult result) => results.Writer.TryWrite(result);

    internal void SendLine(string? line) => Send(new PromptInputResult(line));

    internal async Task NextReadAsync(CancellationToken cancellationToken) => await reads.Reader.ReadAsync(cancellationToken);
}
