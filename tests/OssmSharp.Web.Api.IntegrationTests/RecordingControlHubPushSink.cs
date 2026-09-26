using System.Threading.Channels;

namespace OssmSharp.Web.Api.IntegrationTests;

/// <summary>
/// Records up to 32 outgoing hub pushes and fails when the recording buffer is full.
/// </summary>
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

    /// <summary>
    /// Consumes recorded pushes until one matches the predicate.
    /// </summary>
    /// <param name="predicate">The condition for the push to return.</param>
    /// <returns>The first matching push.</returns>
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
