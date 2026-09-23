using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;

namespace OSSMDotNet.Api.SignalR;

/// <summary>
/// Maps versioned SignalR endpoints for the API boundary.
/// </summary>
public static class ControlHubEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the WebSocket-only v1 control hub with trusted-service authentication.
    /// </summary>
    /// <param name="endpoints">The service endpoint builder.</param>
    /// <returns>The mapped hub endpoint for further conventions.</returns>
    /// <exception cref="ArgumentNullException">The endpoint builder is null.</exception>
    public static HubEndpointConventionBuilder MapControlHubV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapHub<ControlHub>("/hubs/control/v1", options =>
            options.Transports = HttpTransportType.WebSockets)
            .RequireAuthorization(new AuthorizeAttribute
            {
                Policy = ControlServicePolicies.Service,
                AuthenticationSchemes = ServiceKeyAuthenticationDefaults.Scheme
            });
    }
}
