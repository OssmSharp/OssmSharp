using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace OssmSharp.Cli;

internal sealed class ForegroundInputReader : TextReader
{
    private readonly Func<string?> _readLine;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
    private int _started;

    internal ForegroundInputReader(TextReader source) =>
        _readLine = (source ?? throw new ArgumentNullException(nameof(source))).ReadLine;

    public override string? ReadLine() =>
        throw new NotSupportedException("Foreground input must be read asynchronously.");

    public override Task<string?> ReadLineAsync() =>
        ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // Console.In blocks even for ReadLineAsync. The process owns this input-only
            // thread until exit; cancelling the channel consumer never abandons device I/O.
            new Thread(Pump) { IsBackground = true, Name = "OssmSharp CLI input" }.Start();
        }

        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            if (exception.InnerException is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();

            return null;
        }
    }

    private void Pump()
    {
        try
        {
            while (_readLine() is { } line)
                _lines.Writer.WriteAsync(line).AsTask().GetAwaiter().GetResult();

            _lines.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _lines.Writer.TryComplete(exception);
        }
    }
}
