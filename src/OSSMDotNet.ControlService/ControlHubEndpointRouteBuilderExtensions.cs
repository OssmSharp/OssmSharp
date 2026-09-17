using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;

namespace OSSMDotNet.ControlService;

/// <summary>Maps versioned SignalR endpoints for the control-service boundary.</summary>
public static class ControlHubEndpointRouteBuilderExtensions
{
    /// <summary>Maps the authenticated v1 control hub.</summary>
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
