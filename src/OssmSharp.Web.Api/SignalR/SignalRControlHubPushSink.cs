using Microsoft.AspNetCore.SignalR;

namespace OssmSharp.Web.Api.SignalR;

/// <summary>
/// Adapts the SignalR hub context to the bounded dispatcher send contract.
/// </summary>
/// <param name="hub">The hub context used to send callbacks to individual connections.</param>
public sealed class SignalRControlHubPushSink(IHubContext<ControlHub> hub) : IControlHubPushSink
{
    /// <inheritdoc />
    public Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendAsync(method, payload, cancellationToken);
}
