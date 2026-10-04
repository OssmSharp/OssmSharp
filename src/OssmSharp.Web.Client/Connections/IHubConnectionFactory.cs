using Microsoft.AspNetCore.SignalR.Client;

namespace OssmSharp.Web.Client.Connections;

/// <summary>
/// Creates authenticated hub connections for RPC clients.
/// </summary>
public interface IHubConnectionFactory
{
    /// <summary>
    /// Creates a connection
    /// </summary>
    /// <returns>A separate connection owned by the caller.</returns>
    HubConnection Create();

    /// <summary>
    /// Creates a connection authenticated with a guest invitation.
    /// </summary>
    /// <param name="invitationToken">The guest invitation.</param>
    /// <returns>A separate connection owned by the caller.</returns>
    HubConnection CreateForGuest(string invitationToken);
}
