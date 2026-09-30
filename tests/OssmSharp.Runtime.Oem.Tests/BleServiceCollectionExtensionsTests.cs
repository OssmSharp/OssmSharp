using AwesomeAssertions;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

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
        context.Requests.Should().BeEmpty();
        context.ConnectionConfiguration.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(3, 0)]
    public void RegisteredRequestOptionsRejectInvalidLimits(int attempts, int responseTimeoutMilliseconds)
    {
        using var context = new BleTestContext();
        var services = new ServiceCollection();
        services.AddSingleton(context.Manager.Object);
        services.AddOemRuntime();
        services.Configure<OssmDeviceOptions>(options =>
        {
            options.MaximumRequestAttempts = attempts;
            options.ResponseTimeout = TimeSpan.FromMilliseconds(responseTimeoutMilliseconds);
        });
        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IOptions<OssmDeviceOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
        context.Requests.Should().BeEmpty();
    }

    [Fact]
    public void PortableTargetRequiresAnExplicitBluetoothManager()
    {
        var services = new ServiceCollection();

        var register = () => services.AddOemRuntime();

        register.Should().Throw<PlatformNotSupportedException>();
    }
}
