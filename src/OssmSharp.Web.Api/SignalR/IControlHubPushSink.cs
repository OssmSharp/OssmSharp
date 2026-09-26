namespace OssmSharp.Web.Api.SignalR;

/// <summary>
/// Writes one already-authorized callback to a hub connection.
/// </summary>
public interface IControlHubPushSink
{
    /// <summary>
    /// Sends a callback without retaining application state or credentials.
    /// </summary>
    /// <param name="connectionId">The subscribed SignalR connection to receive the callback.</param>
    /// <param name="method">The callback name expected by that connection.</param>
    /// <param name="payload">The bounded callback body.</param>
    Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken);
}
