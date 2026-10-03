using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class BleServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task FactoryRequiresCallerSelectedIdentifierWithoutDiscovery(string identifier)
    {
        using var context = new BleTestContext();
        var services = new ServiceCollection();
        services.AddSingleton(context.Manager.Object);
        services.AddOemRuntime();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<Func<string, DeviceBase>>();

        Assert.Throws<ArgumentException>(() => factory(identifier));

        context.Manager.Verify(manager => manager.Scan(It.IsAny<ScanConfig>()), Times.Never);
        Assert.Empty(context.Requests);
        context.Peripheral.Verify(peripheral => peripheral.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
    }

    [Fact]
    public async Task UnknownExplicitIdentifierDoesNotFallBackToDiscovery()
    {
        using var context = new BleTestContext();
        var services = new ServiceCollection();
        services.AddSingleton(context.Manager.Object);
        services.AddOemRuntime();
        await using var provider = services.BuildServiceProvider();
        await using var device = provider.GetRequiredService<Func<string, DeviceBase>>()("unavailable-device");

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.ConnectAsync(TestContext.Current.CancellationToken));

        context.Manager.Verify(manager => manager.GetKnownPeripheral("unavailable-device"), Times.Once);
        context.Manager.Verify(manager => manager.Scan(It.IsAny<ScanConfig>()), Times.Never);
        Assert.Empty(context.Requests);
    }

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
        provider.GetRequiredService<IDeviceDiscovery>().Should().BeOfType<DeviceDiscovery>()
            .And.BeSameAs(provider.GetRequiredService<IDeviceDiscovery>());
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
