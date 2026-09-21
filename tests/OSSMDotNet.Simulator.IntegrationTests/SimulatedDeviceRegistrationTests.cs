using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;

namespace OSSMDotNet.Simulator.IntegrationTests;

public sealed class SimulatedDeviceRegistrationTests
{
    [Fact]
    public async Task RegistrationUsesOneDeviceForThePublicContractAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOssmSimulator(options => options.ExecutablePath = SimulatorPath());
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
        services.AddOssmSimulator(options => options.ExecutablePath = SimulatorPath());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOssmSimulator(options => options.ExecutablePath = SimulatorPath()));

        Assert.Equal("An IOssmDevice implementation is already registered.", exception.Message);
    }

    private static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(SimulatedDeviceRegistrationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");
        return path;
    }
}
