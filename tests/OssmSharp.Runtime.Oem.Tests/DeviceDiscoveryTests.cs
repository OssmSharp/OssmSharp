using System.Reactive.Linq;
using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Moq;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceDiscoveryTests : MockerTestBase<DeviceDiscovery>, IAsyncDisposable
{
    private BleTestContext context = null!;

    protected override Action<Mocker> SetupMocksAction => mocks => context = new(mocks);

    public ValueTask DisposeAsync()
    {
        try
        {
            context.Dispose();
        }
        finally
        {
            base.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ScanIsBoundedServiceFilteredDistinctAndDoesNotConnect()
    {
        var scan = Component.ScanAsync(TestContext.Current.CancellationToken);
        var advertisement = Mocks.GetOrCreateMock<IAdvertisementData>().Instance;
        context.ScanResults.OnNext(new(context.Peripheral.Object, -40, advertisement));
        context.ScanResults.OnNext(new(context.Peripheral.Object, -50, advertisement));

        context.Clock.Advance(TimeSpan.FromSeconds(4));
        scan.IsCompleted.Should().BeFalse();
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        (await scan).Should().Equal(new DiscoveredDevice("device-1", "OSSM"));
        context.ScanConfiguration!.ServiceUuids.Should().Equal(BleProtocolConstants.Service);
        context.ScanDisposals.Should().Be(1);
        context.ScanResults.HasObservers.Should().BeFalse();
        Mocks.Verify<IPeripheral>(peripheral => peripheral.Connect(FastArg.Any<ConnectionConfig>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task FindFirstReturnsFirstDeviceBeforeTimeoutAndReleasesSubscription()
    {
        var scan = Component.FindFirstAsync(TestContext.Current.CancellationToken);
        var advertisement = Mocks.GetOrCreateMock<IAdvertisementData>().Instance;

        try
        {
            scan.IsCompleted.Should().BeFalse();
            context.ScanResults.OnNext(new(context.Peripheral.Object, -40, advertisement));

            context.ScanResults.HasObservers.Should().BeFalse();
            (await scan.WaitAsync(TestContext.Current.CancellationToken)).Should().Be(new DiscoveredDevice("device-1", "OSSM"));
            context.ScanDisposals.Should().Be(1);
            context.ScanConfiguration!.ServiceUuids.Should().Equal(BleProtocolConstants.Service);
            Mocks.Verify<IPeripheral>(peripheral => peripheral.Connect(FastArg.Any<ConnectionConfig>()), TimesSpec.NeverCalled);
        }
        finally
        {
            context.Clock.Advance(TimeSpan.FromSeconds(5));
            await scan;
        }
    }

    [Fact]
    public async Task FindFirstReturnsNullOnTimeoutAndReleasesSubscription()
    {
        var scan = Component.FindFirstAsync(TestContext.Current.CancellationToken);

        context.Clock.Advance(TimeSpan.FromSeconds(4));
        scan.IsCompleted.Should().BeFalse();
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        (await scan).Should().BeNull();
        context.ScanDisposals.Should().Be(1);
        context.ScanResults.HasObservers.Should().BeFalse();
        Mocks.Verify<IPeripheral>(peripheral => peripheral.Connect(FastArg.Any<ConnectionConfig>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyScanReturnsNoDevicesAndReleasesSubscription(bool stopAfterFirst)
    {
        var scan = Component.ScanAsync(stopAfterFirst, TestContext.Current.CancellationToken);

        context.Clock.Advance(TimeSpan.FromSeconds(5));

        (await scan).Should().BeEmpty();
        context.ScanDisposals.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPropagatesAndReleasesScan(bool stopAfterFirst)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scan = Component.ScanAsync(stopAfterFirst, cancellation.Token);

        await cancellation.CancelAsync();

        var result = () => scan;
        await result.Should().ThrowAsync<OperationCanceledException>();
        context.ScanDisposals.Should().Be(1);
        context.ScanResults.HasObservers.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BluetoothScanErrorPropagatesUnchanged(bool stopAfterFirst)
    {
        var scan = Component.ScanAsync(stopAfterFirst, TestContext.Current.CancellationToken);
        var failure = new IOException("Scan failed.");

        context.ScanResults.OnError(failure);

        var result = () => scan;
        (await result.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        context.ScanDisposals.Should().Be(1);
    }

    [Fact]
    public async Task DeniedAccessDoesNotStartScan()
    {
        context.Manager.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Denied));

        var scan = () => Component.ScanAsync(TestContext.Current.CancellationToken);

        await scan.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Bluetooth*permission*");
        Mocks.Verify<IBleManager>(manager => manager.Scan(FastArg.Any<ScanConfig>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task ConcurrentScanDoesNotInterfereWithTheOwner()
    {
        var first = Component.ScanAsync(TestContext.Current.CancellationToken);

        try
        {
            var second = () => Component.ScanAsync(TestContext.Current.CancellationToken);
            await second.Should().ThrowAsync<InvalidOperationException>();
            context.ScanDisposals.Should().Be(0);
        }
        finally
        {
            context.Clock.Advance(TimeSpan.FromSeconds(5));
            await first;
        }

        context.ScanDisposals.Should().Be(1);
    }
}
