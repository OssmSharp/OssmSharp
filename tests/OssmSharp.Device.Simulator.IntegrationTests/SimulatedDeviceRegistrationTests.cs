using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OssmSharp.Device.Simulator.IntegrationTests;

/// <summary>
/// Verifies that simulator registration shares one device instance across its hosted and public contracts.
/// </summary>
public sealed class SimulatedDeviceRegistrationTests
{
    private const string SimulatorEndpoint = "tcp://127.0.0.1:1";

    [Fact]
    public async Task RegistrationUsesOneDeviceForThePublicContractAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOssmSimulator(options => options.Endpoint = SimulatorEndpoint);
        await using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<SimulatedDevice>();
        var device = provider.GetRequiredService<IOssmDevice>();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());

        Assert.Same(concrete, device);
        Assert.Same(concrete, hosted);
    }

    [Fact]
    public void NullConfigurationIsRejectedBeforeRegistration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddOssmSimulator(null!));
        Assert.Empty(services);
    }

    [Fact]
    public void DuplicateDeviceRegistrationIsRejected()
    {
        var services = new ServiceCollection();
        services.AddOssmSimulator(options => options.Endpoint = SimulatorEndpoint);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOssmSimulator(options => options.Endpoint = SimulatorEndpoint));

        Assert.Equal("An IOssmDevice implementation is already registered.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tcp://192.0.2.1:1234")]
    public async Task InvalidEndpointFailsHostStartup(string endpoint)
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddOssmSimulator(options => options.Endpoint = endpoint))
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
