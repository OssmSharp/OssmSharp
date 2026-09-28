using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Device.Runtime;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Device.Oem.Tests;

public sealed class BleServiceCollectionExtensionsTests
{
    [Fact]
    public async Task RegistersDiscoveryAndCallerOwnedDeviceFactoryWithoutConnecting()
    {
        using var context = new BleTestContext();
        var services = new ServiceCollection();
        services.AddSingleton(context.Manager.Object);
        services.AddOemRuntime().Should().BeSameAs(services);
        services.AddOemRuntime();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IBleManager>().Should().BeSameAs(context.Manager.Object);
        provider.GetRequiredService<DeviceDiscovery>().Should().BeSameAs(provider.GetRequiredService<DeviceDiscovery>());
        var factory = provider.GetRequiredService<Func<string, DeviceBase>>();
        await using var first = factory("device-1");
        await using var second = factory("device-2");

        first.Should().BeOfType<OssmDevice>();
        first.Should().NotBeSameAs(second);
        first.IsArmed.Should().BeFalse();
        context.Reads.Should().Be(0);
        context.Writes.Should().BeEmpty();
    }

    [Fact]
    public void PortableTargetRequiresAnExplicitBluetoothManager()
    {
        var services = new ServiceCollection();

        var register = () => services.AddOemRuntime();

        register.Should().Throw<PlatformNotSupportedException>();
    }
}
