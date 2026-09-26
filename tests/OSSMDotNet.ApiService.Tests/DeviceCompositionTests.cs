using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Simulator;
using OssmSharp.Web.Application.Models;

namespace OSSMDotNet.ApiService.Tests;

/// <summary>
/// Verifies that the hosted device and authority share one simulator instance.
/// </summary>
public sealed class DeviceCompositionTests
{
    [Fact]
    public async Task AuthorityUsesOneSimulatorForConcreteContractAndHostedDevice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<ControlServiceOptions>().Configure(options =>
            options.SimulatorPath = Environment.ProcessPath!);
        services.AddApiComposition();
        services.AddApiHub();
        await using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<SimulatedDevice>();
        var device = provider.GetRequiredService<IOssmDevice>();
        var hostedDevice = Assert.Single(
            provider.GetServices<IHostedService>().OfType<SimulatedDevice>());

        Assert.Same(concrete, device);
        Assert.Same(concrete, hostedDevice);
    }
}
