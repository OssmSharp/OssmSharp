using System.Threading.Channels;

namespace OssmSharp.Cli.Tests;

internal sealed class CommandInput : TextReader
{
    private readonly Channel<string?> lines = Channel.CreateUnbounded<string?>();
    private readonly Channel<bool> reads = Channel.CreateUnbounded<bool>();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        reads.Writer.TryWrite(true);
        return await lines.Reader.ReadAsync(cancellationToken);
    }

    internal void Send(string? line) => lines.Writer.TryWrite(line);

    internal async Task NextReadAsync(CancellationToken cancellationToken) => await reads.Reader.ReadAsync(cancellationToken);
}
