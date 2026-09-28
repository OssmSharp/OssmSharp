using System.Reactive.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shiny;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Device.Oem.Tests;

public sealed class DeviceDiscoveryTests
{
    [Fact]
    public async Task ScanIsBoundedServiceFilteredDistinctAndDoesNotConnect()
    {
        using var context = new BleTestContext();
        var clock = new FakeTimeProvider();
        var discovery = new DeviceDiscovery(context.Manager.Object, clock);
        var scan = discovery.ScanAsync(TestContext.Current.CancellationToken);
        var advertisement = context.Mocks.GetOrCreateMock<IAdvertisementData>().Instance;
        context.ScanResults.OnNext(new(context.Peripheral.Object, -40, advertisement));
        context.ScanResults.OnNext(new(context.Peripheral.Object, -50, advertisement));

        clock.Advance(TimeSpan.FromSeconds(4));
        scan.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));

        (await scan).Should().Equal(new DiscoveredDevice("device-1", "OSSM"));
        context.ScanConfiguration!.ServiceUuids.Should().Equal(BleProtocol.Service);
        context.ScanDisposals.Should().Be(1);
        context.ScanResults.HasObservers.Should().BeFalse();
        context.Peripheral.Verify(peripheral => peripheral.Connect(It.IsAny<ConnectionConfig>()), Times.Never);
    }

    [Fact]
    public async Task EmptyScanReturnsNoDevicesAndReleasesSubscription()
    {
        using var context = new BleTestContext();
        var clock = new FakeTimeProvider();
        var scan = new DeviceDiscovery(context.Manager.Object, clock).ScanAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(5));

        (await scan).Should().BeEmpty();
        context.ScanDisposals.Should().Be(1);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesScan()
    {
        using var context = new BleTestContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scan = new DeviceDiscovery(context.Manager.Object, new FakeTimeProvider()).ScanAsync(cancellation.Token);

        await cancellation.CancelAsync();

        var result = () => scan;
        await result.Should().ThrowAsync<OperationCanceledException>();
        context.ScanDisposals.Should().Be(1);
        context.ScanResults.HasObservers.Should().BeFalse();
    }

    [Fact]
    public async Task BluetoothScanErrorPropagatesUnchanged()
    {
        using var context = new BleTestContext();
        var scan = new DeviceDiscovery(context.Manager.Object, new FakeTimeProvider()).ScanAsync(TestContext.Current.CancellationToken);
        var failure = new IOException("Scan failed.");

        context.ScanResults.OnError(failure);

        var result = () => scan;
        (await result.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        context.ScanDisposals.Should().Be(1);
    }

    [Fact]
    public async Task DeniedAccessDoesNotStartScan()
    {
        using var context = new BleTestContext();
        context.Manager.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Denied));
        var discovery = new DeviceDiscovery(context.Manager.Object, new FakeTimeProvider());

        var scan = () => discovery.ScanAsync(TestContext.Current.CancellationToken);

        await scan.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Bluetooth*permission*");
        context.Manager.Verify(manager => manager.Scan(It.IsAny<ScanConfig>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentScanDoesNotInterfereWithTheOwner()
    {
        using var context = new BleTestContext();
        var clock = new FakeTimeProvider();
        var discovery = new DeviceDiscovery(context.Manager.Object, clock);
        var first = discovery.ScanAsync(TestContext.Current.CancellationToken);

        try
        {
            var second = () => discovery.ScanAsync(TestContext.Current.CancellationToken);
            await second.Should().ThrowAsync<InvalidOperationException>();
            context.ScanDisposals.Should().Be(0);
        }
        finally
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await first;
        }

        context.ScanDisposals.Should().Be(1);
    }
}
