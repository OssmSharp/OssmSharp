#if WINDOWS || MACOS
using System.Text;

namespace OssmSharp.Cli.IntegrationTests;

internal sealed class SignalingTextWriter(string? signalText = null) : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _content = new();
    private readonly TaskCompletionSource _written =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Encoding Encoding => Encoding.UTF8;

    public Task Written => _written.Task;

    public string Content
    {
        get
        {
            lock (_gate)
                return _content.ToString();
        }
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value) => Append(value);

    public override Task WriteLineAsync(string? value)
    {
        Append(value + NewLine);
        return Task.CompletedTask;
    }

    public override string ToString() => Content;

    private void Append(string? value)
    {
        lock (_gate)
        {
            _content.Append(value);
            if (signalText is null || _content.ToString().Contains(signalText, StringComparison.Ordinal))
                _written.TrySetResult();
        }
    }
}
#endif
