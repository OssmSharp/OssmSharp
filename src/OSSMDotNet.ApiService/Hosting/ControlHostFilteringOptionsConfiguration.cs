using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.ApiService.Hosting;

/// <summary>
/// Limits accepted Host headers to the direct endpoint hostname without using Host as authentication.
/// </summary>
/// <param name="options">The validated service hostname configuration.</param>
public sealed class ControlHostFilteringOptionsConfiguration(IOptions<ControlServiceOptions> options)
    : IConfigureOptions<HostFilteringOptions>
{
    /// <inheritdoc />
    public void Configure(HostFilteringOptions hostFilteringOptions)
    {
        hostFilteringOptions.AllowedHosts.Clear();
        hostFilteringOptions.AllowedHosts.Add(options.Value.HostName);
    }
}
