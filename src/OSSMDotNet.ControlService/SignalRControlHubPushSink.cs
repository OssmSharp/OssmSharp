using Microsoft.AspNetCore.SignalR;

namespace OSSMDotNet.ControlService;

/// <summary>Adapts the SignalR hub context to the bounded dispatcher send seam.</summary>
public sealed class SignalRControlHubPushSink(IHubContext<ControlHub> hub) : IControlHubPushSink
{
    /// <inheritdoc />
    public Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendAsync(method, payload, cancellationToken);
}
