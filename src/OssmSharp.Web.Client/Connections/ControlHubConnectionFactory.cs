using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Client.Connections;

/// <summary>
/// Creates isolated, authenticated SignalR hub connections for server-side consumers.
/// </summary>
public sealed class ControlHubConnectionFactory
{
    private readonly ControlClientOptions _options;

    /// <summary>
    /// Creates a connection factory without operational logging.
    /// </summary>
    /// <param name="options">The validated endpoint and service credentials for new connections.</param>
    public ControlHubConnectionFactory(IOptions<ControlClientOptions> options)
        : this(options, NullLogger<ControlHubConnectionFactory>.Instance) { }

    /// <summary>
    /// Creates a connection factory with the host's operational logger.
    /// </summary>
    /// <param name="options">The validated endpoint and service credentials for new connections.</param>
    /// <param name="logger">The host's operational logger.</param>
    public ControlHubConnectionFactory(IOptions<ControlClientOptions> options, ILogger<ControlHubConnectionFactory> logger)
    {
        _options = options.Value;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal ILogger Logger { get; }

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

    private HubConnection Create(string authorization)
    {
        var origin = new Uri(_options.ControlOrigin, UriKind.Absolute);
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
