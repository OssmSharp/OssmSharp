using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.Api.SignalR;

/// <summary>
/// Applies validated control-service liveness settings to SignalR hubs.
/// </summary>
/// <param name="options">The validated control-service liveness settings.</param>
public sealed class SignalRHubOptionsConfiguration(IOptions<ControlServiceOptions> options) : IConfigureOptions<HubOptions>
{
    /// <inheritdoc />
    public void Configure(HubOptions hubOptions)
    {
        hubOptions.ClientTimeoutInterval = TimeSpan.FromSeconds(options.Value.ClientTimeoutSeconds);
        hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(options.Value.KeepAliveSeconds);
    }
}
