using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Client.Connections;

/// <summary>
/// Creates isolated, authenticated SignalR hub connections for server-side consumers.
/// </summary>
public sealed class SignalRHubConnectionFactory : IHubConnectionFactory
{
    private readonly RpcClientOptions _options;

    /// <summary>
    /// Creates a connection factory for the configured endpoint and service credentials.
    /// </summary>
    /// <param name="options">The validated endpoint and service credentials for new connections.</param>
    public SignalRHubConnectionFactory(IOptions<RpcClientOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Creates a connection configured for WebSockets-only control traffic without automatic reconnect.
    /// </summary>
    /// <returns>A separate authenticated hub connection owned by its caller.</returns>
    public HubConnection Create()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ServiceKey);
        return Create($"ServiceKey {_options.ServiceKey}");
    }

    /// <summary>
    /// Creates a guest connection using an invitation.
    /// </summary>
    /// <param name="invitationToken">The guest's invitation, sent only to the configured API.</param>
    /// <returns>A connection the caller must dispose. It does not reconnect automatically.</returns>
    public HubConnection CreateForGuest(string invitationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationToken);
        return Create($"Bearer {invitationToken}");
    }

    /// <inheritdoc />
    public HubConnection CreateForGuestSession(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        return Create($"Session {sessionToken}");
    }

    private HubConnection Create(string authorization)
    {
        var origin = new Uri(_options.EndpointUri, UriKind.Absolute);
        var hub = new Uri(origin, "/hubs/control/v2");
        return new HubConnectionBuilder()
            .WithUrl(hub, connection =>
            {
                connection.Transports = HttpTransportType.WebSockets;
                connection.SkipNegotiation = false;
                connection.Headers.Add("Authorization", authorization);
            })
            .WithServerTimeout(TimeSpan.FromSeconds(_options.ServerTimeoutSeconds))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
            .Build();
    }
}
