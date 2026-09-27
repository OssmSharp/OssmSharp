#if !WINDOWS && !MACOS
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Cli.Commands;
using OssmSharp.Cli.Interop;
using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

public sealed class CliPlatformServicesTests
{
    [Fact]
    public async Task PortableCompositionReportsUnsupportedOperationsExplicitly()
    {
        var services = new ServiceCollection();
        using var platformLifetime = services.AddCliPlatformServices();
        using var provider = services.BuildServiceProvider();

        Assert.Null(platformLifetime);
        var discovery = provider.GetRequiredService<IDeviceDiscovery>();
        var identityProvider = provider.GetRequiredService<IDeviceProvider>();
        var scanFailure = await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            discovery.ScanAsync(TestContext.Current.CancellationToken));
        var readFailure = await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            identityProvider.ReadInformationAsync(
                new TestDeviceIdentifier("portable-id", "Device", new object()),
                TestContext.Current.CancellationToken));

        Assert.Equal("Stock BLE discovery requires a native Windows or macOS build.", scanFailure.Message);
        Assert.Equal("Stock BLE details require a Windows or macOS native BLE build.", readFailure.Message);
    }

    [Fact]
    public async Task PortableListAndGetPreserveUnsupportedCommandMessages()
    {
        var services = new ServiceCollection();
        using var platformLifetime = services.AddCliPlatformServices();
        using var provider = services.BuildServiceProvider();
        var discovery = provider.GetRequiredService<IDeviceDiscovery>();
        var identityProvider = provider.GetRequiredService<IDeviceProvider>();

        using var listOutput = new StringWriter();
        using var listError = new StringWriter();
        var list = new DeviceListCommand(discovery, listOutput, listError);
        var listExitCode = await list.Parse(["list"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var getOutput = new StringWriter();
        using var getError = new StringWriter();
        var get = new DeviceGetCommand(discovery, identityProvider, getOutput, getError);
        var getExitCode = await get.Parse(["get", "portable-id"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, listExitCode);
        Assert.Equal("Stock BLE discovery requires a native Windows or macOS build." + Environment.NewLine, listError.ToString());
        Assert.Equal("Detecting devices. Please wait ..." + Environment.NewLine, listOutput.ToString());
        Assert.Equal(1, getExitCode);
        Assert.Equal(
            "Stock BLE details require a Windows or macOS native BLE build." + Environment.NewLine,
            getError.ToString());
        Assert.Equal(string.Empty, getOutput.ToString());
    }
}
#endif
