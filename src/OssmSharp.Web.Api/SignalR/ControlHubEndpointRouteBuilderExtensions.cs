using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;

namespace OssmSharp.Web.Api.SignalR;

/// <summary>
/// Maps versioned SignalR endpoints for the API boundary.
/// </summary>
public static class ControlHubEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the v2 WebSocket hub for administrators and invited guests.
    /// </summary>
    /// <param name="endpoints">The service endpoint builder.</param>
    /// <returns>The mapped hub endpoint for further conventions.</returns>
    /// <exception cref="ArgumentNullException">The endpoint builder is null.</exception>
    public static HubEndpointConventionBuilder MapControlHubV2(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapHub<ControlHub>("/hubs/control/v2", options =>
            options.Transports = HttpTransportType.WebSockets)
            .RequireAuthorization(new AuthorizeAttribute
            {
                Policy = ControlServicePolicies.Service,
            });
    }
}
