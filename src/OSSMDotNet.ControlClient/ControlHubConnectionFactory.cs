using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.ControlClient;

/// <summary>Creates isolated, authenticated SignalR hub connections for server-side consumers.</summary>
public sealed class ControlHubConnectionFactory(IOptions<ControlClientOptions> options)
{
    private readonly ControlClientOptions _options = options.Value;

    /// <summary>Creates a connection configured for WebSockets-only control traffic without automatic reconnect.</summary>
    public HubConnection Create()
    {
        var origin = new Uri(_options.ControlOrigin, UriKind.Absolute);
        var hub = new Uri(origin, "/hubs/control/v1");
        return new HubConnectionBuilder()
            .WithUrl(hub, connection =>
            {
                connection.Transports = HttpTransportType.WebSockets;
                connection.SkipNegotiation = false;
                connection.Headers.Add("Authorization", $"ServiceKey {_options.ServiceKey}");
            })
            .WithServerTimeout(TimeSpan.FromSeconds(_options.ServerTimeoutSeconds))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
            .Build();
    }
}
