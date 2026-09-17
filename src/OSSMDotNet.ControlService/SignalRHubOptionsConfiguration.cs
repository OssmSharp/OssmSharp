using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Applies validated control-service liveness settings to SignalR hubs.</summary>
public sealed class SignalRHubOptionsConfiguration(IOptions<ControlServiceOptions> options) : IConfigureOptions<HubOptions>
{
    public void Configure(HubOptions hubOptions)
    {
        hubOptions.ClientTimeoutInterval = TimeSpan.FromSeconds(options.Value.ClientTimeoutSeconds);
        hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(options.Value.KeepAliveSeconds);
    }
}
