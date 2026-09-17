using System.Threading.Channels;
using OSSMDotNet.ControlService;

namespace OSSMDotNet.ControlService.IntegrationTests;

internal sealed class RecordingControlHubPushSink : IControlHubPushSink
{
    private readonly Channel<(string ConnectionId, string Method, object Payload)> _calls =
        Channel.CreateBounded<(string ConnectionId, string Method, object Payload)>(
            new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });

    public Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_calls.Writer.TryWrite((connectionId, method, payload)))
            throw new IOException("push-recording-capacity");
        return Task.CompletedTask;
    }

    internal async Task<(string ConnectionId, string Method, object Payload)> ReadUntilAsync(
        Func<(string ConnectionId, string Method, object Payload), bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var call = await _calls.Reader.ReadAsync(cancellationToken);
            if (predicate(call))
                return call;
        }
    }
}
