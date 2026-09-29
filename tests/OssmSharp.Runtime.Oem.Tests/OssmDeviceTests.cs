using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using AwesomeAssertions;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceTests
{
    public static bool RunningOnMacOS => OperatingSystem.IsMacOS();

    [Fact]
    public async Task SetupFailureRetainsOwnershipUntilDisconnectAcknowledged()
    {
        using var context = new BleTestContext { CompleteDisconnect = false };
        var failure = new IOException("Connection setup failed after issuance.");
        var nativeState = ConnectionState.Disconnected;
        context.Peripheral.SetupGet(peripheral => peripheral.Status).Returns(() => nativeState);
        context.Peripheral.Setup(peripheral => peripheral.WhenStatusChanged()).Returns(context.Status.DistinctUntilChanged());
        context.Peripheral.Setup(peripheral => peripheral.Connect(Moq.It.IsAny<ConnectionConfig>())).Callback(() =>
        {
            nativeState = ConnectionState.Connecting;
            throw failure;
        });
        await using var device = context.CreateDevice();
        var connect = device.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            context.DisconnectCalls.Should().Be(1);
            connect.IsCompleted.Should().BeFalse();
            nativeState = ConnectionState.Disconnected;
            await using var contender = context.CreateDevice();
            var competing = () => contender.ConnectAsync(TestContext.Current.CancellationToken);
            await competing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned*");
            context.DisconnectCalls.Should().Be(1);
        }
        finally
        {
            nativeState = ConnectionState.Disconnected;
            await new BleLifecycleDelegate().OnPeripheralStateChanged(context.Peripheral.Object);
            var result = () => connect;
            (await result.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        }
    }

    [Fact]
    public async Task ProviderReadErrorPropagatesWithoutCachedSuccess()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var failure = new IOException("GATT read failed.");
        context.Read = () => Observable.Throw<BleCharacteristicResult>(failure);

        var read = () => device.GetStateAsync(true, token);

        (await read.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 50, 10, 50)));
    }

    [Fact]
    public async Task ProviderWriteErrorPreservesPartialProgressAndDoesNotRetry()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var failure = new IOException("GATT write failed.");
        context.Write = data => context.Writes.Count == 2
            ? Observable.Throw<BleCharacteristicResult>(failure)
            : Observable.Return(BleTestContext.StateResult(data));

        var set = () => device.SetAsync(new(1, 20, 30, 40, 50), token);
        var result = (await set.Should().ThrowAsync<DeviceWriteException>()).Which;

        result.CommandsWritten.Should().Be(1);
        result.InnerException.Should().BeSameAs(failure);
        context.Writes.Should().Equal("set:pattern:1", "set:stroke:30");
        device.IsArmed.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public async Task RejectsOutOfBoundsPayloads(int length)
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        context.StateData = new byte[length];

        var read = () => device.GetStateAsync(true, token);

        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*512 bytes*");
    }

    [Fact]
    public async Task UnavailableSelectionDoesNotConnectOrDisconnectAnotherDevice()
    {
        using var context = new BleTestContext();
        context.Manager.Setup(manager => manager.GetKnownPeripheral("device-1")).Returns((IPeripheral?)null);
        await using var device = context.CreateDevice();

        var connect = () => device.ConnectAsync(TestContext.Current.CancellationToken);
        await connect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unavailable*");
        await device.DisposeAsync();

        context.ConnectionConfiguration.Should().BeNull();
        context.DisconnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task CancellingSubscriptionSetupDisconnectsBeforeReturning()
    {
        using var context = new BleTestContext { CompleteSubscription = false };
        await using var device = context.CreateDevice();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = device.ConnectAsync(cancellation.Token);

        await cancellation.CancelAsync();
        var result = () => connect;
        await result.Should().ThrowAsync<OperationCanceledException>();

        context.DisconnectCalls.Should().Be(1);
        context.NotificationDisposals.Should().Be(1);
        context.Reads.Should().Be(0);
        context.Status.Value.Should().Be(ConnectionState.Disconnected);
    }

    [Fact(Skip = "Requires the macOS read-isolation path.", SkipUnless = nameof(RunningOnMacOS))]
    public async Task MacOSReadDetachesNotificationsUntilReadSettlesAndRestoresThem()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var read = new Subject<BleCharacteristicResult>();
        context.Read = () =>
        {
            context.Notifications.HasObservers.Should().BeFalse();
            issued.TrySetResult();
            return read;
        };

        var live = device.GetStateAsync(true, token);
        try
        {
            await issued.Task.WaitAsync(token);
            context.Notifications.OnNext(BleTestContext.StateResult(Encoding.UTF8.GetBytes(BleTestContext.InitialState)));
            live.IsCompleted.Should().BeFalse();
        }
        finally
        {
            read.OnNext(BleTestContext.StateResult(Encoding.UTF8.GetBytes(BleTestContext.InitialState)));
            read.OnCompleted();
            await live;
        }

        context.Notifications.HasObservers.Should().BeTrue();
    }

    [Fact]
    public async Task RejectsSecondOwnerDuringPendingConnection()
    {
        using var context = new BleTestContext();
        context.Peripheral.Setup(peripheral => peripheral.Connect(Moq.It.IsAny<ConnectionConfig>())).Callback(() => { });
        await using var first = context.CreateDevice();
        await using var second = context.CreateDevice();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstConnect = first.ConnectAsync(cancellation.Token);
        var secondConnect = second.ConnectAsync(cancellation.Token);

        try
        {
            secondConnect.IsCompleted.Should().BeTrue();
            var result = () => secondConnect;
            await result.Should().ThrowAsync<InvalidOperationException>();
            context.DisconnectCalls.Should().Be(0);
        }
        finally
        {
            context.Status.OnNext(ConnectionState.Connected);
            await firstConnect;
            try
            {
                await secondConnect;
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task NegativeSubscriptionAcknowledgementFailsConnectionAndCleansUp()
    {
        using var context = new BleTestContext { CompleteSubscription = false };
        await using var device = context.CreateDevice();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = device.ConnectAsync(cancellation.Token);

        try
        {
            context.SubscriptionChanges.OnNext(BleTestContext.StateCharacteristic with { IsNotifying = false });
            context.NotificationDisposals.Should().Be(1);
            var result = () => connect;
            await result.Should().ThrowAsync<IOException>();
            context.DisconnectCalls.Should().Be(1);
            context.Reads.Should().Be(0);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await connect;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
            }
        }
    }

    [Fact]
    public async Task ConnectsSubscribesAndReadsWithoutArmingOrWriting()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;

        await device.ConnectAsync(token);

        device.IsArmed.Should().BeFalse();
        context.ConnectionConfiguration.Should().Be(new ConnectionConfig(false));
        context.Writes.Should().BeEmpty();
        context.Reads.Should().Be(1);
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 50, 10, 50)));
    }

    [Fact]
    public async Task InitialCompactStateIsReplacedByFullReadWithoutArmingOrWriting()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        context.Read = () => Observable.Return(BleTestContext.StateResult(context.Reads == 1
            ? Encoding.UTF8.GetBytes("""{"state":"menu","v":1,"sequence":0,"uptimeMs":1,"activeOperation":null,"lease":{"active":false}}""")
            : context.StateData));

        await device.ConnectAsync(token);

        context.Reads.Should().Be(2);
        context.Writes.Should().BeEmpty();
        device.IsArmed.Should().BeFalse();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 50, 10, 50)));
    }

    [Fact]
    public async Task SendsOneWritePerPrimitiveAndStopUsesTheSameCharacteristic()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);

        (await device.SetAsync(new(2, 20, 30, 40, 50), token)).Should().Be(new SettingsResult(SettingsOutcome.Completed, 5));
        await device.EmergencyStopAsync(token);

        context.Writes.Should().Equal("set:pattern:2", "set:stroke:30", "set:depth:40", "set:sensation:50", "set:speed:20", "set:speed:0");
        device.IsArmed.Should().BeFalse();
    }

    [Theory]
    [InlineData("ok:boot")]
    [InlineData("ok:set:speed:20")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"pattern\":0,\"speed\":101,\"stroke\":50,\"depth\":10,\"sensation\":50}")]
    [InlineData("{\"pattern\":0,\"speed\":12.5,\"stroke\":50,\"depth\":10,\"sensation\":50}")]
    [InlineData("{\"pattern\":0,\"speed\":0,\"speed\":20,\"stroke\":50,\"depth\":10,\"sensation\":50}")]
    public async Task RejectsMalformedStateWithoutReturningCachedSuccess(string payload)
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        context.StateData = Encoding.UTF8.GetBytes(payload);

        var read = () => device.GetStateAsync(true, token);
        await read.Should().ThrowAsync<InvalidDataException>();

        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 50, 10, 50)));
    }

    [Fact]
    public async Task MalformedNotificationDisarmsAndPreventsReset()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);

        var disposalsBefore = context.NotificationDisposals;
        context.Notifications.OnNext(BleTestContext.StateResult(null));

        device.IsArmed.Should().BeFalse();
        context.NotificationDisposals.Should().Be(disposalsBefore + 1);
        var reset = () => device.ResetAsync(token);
        await reset.Should().ThrowAsync<InvalidDataException>();
        context.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task NotificationUpdatesRuntimeStateAndDetectsExternalChanges()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var state = "{\"pattern\":0,\"speed\":25,\"stroke\":50,\"depth\":10,\"sensation\":50}";

        context.Notifications.OnNext(BleTestContext.StateResult(Encoding.UTF8.GetBytes(state)));

        device.IsArmed.Should().BeFalse();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 25, 50, 10, 50)));
        context.Reads.Should().Be(1);
    }

    [Fact]
    public async Task DisconnectDisarmsAndPreventsFurtherSubmission()
    {
        using var context = new BleTestContext();
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);

        context.Status.OnNext(ConnectionState.Disconnected);

        device.IsArmed.Should().BeFalse();
        var set = () => device.SetAsync(new(0, 20, 30, 40, 50), token);
        await set.Should().ThrowAsync<IOException>();
        context.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitsForSubscriptionBeforeInitialRead()
    {
        using var context = new BleTestContext { CompleteSubscription = false };
        await using var device = context.CreateDevice();
        var connect = device.ConnectAsync(TestContext.Current.CancellationToken);

        try
        {
            connect.IsCompleted.Should().BeFalse();
            context.Reads.Should().Be(0);
        }
        finally
        {
            context.CompleteSubscription = true;
            context.SubscriptionChanges.OnNext(BleTestContext.StateCharacteristic);
            await connect;
        }

        context.Reads.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAwaitsIssuedWriteAndDisconnectAcknowledgement()
    {
        using var context = new BleTestContext { CompleteDisconnect = false };
        using var write = new Subject<BleCharacteristicResult>();
        var disconnectIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Callback(() => disconnectIssued.TrySetResult());
        var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        context.Write = _ => write;
        var disposalsBefore = context.NotificationDisposals;
        var set = device.SetAsync(new(0, 20, 30, 40, 50), token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            dispose.IsCompleted.Should().BeFalse();
            context.NotificationDisposals.Should().Be(disposalsBefore);
            write.OnNext(BleTestContext.StateResult([]));
            write.OnCompleted();
            await disconnectIssued.Task.WaitAsync(token);
            dispose.IsCompleted.Should().BeFalse();
            context.NotificationDisposals.Should().Be(disposalsBefore + 1);
        }
        finally
        {
            write.OnNext(BleTestContext.StateResult([]));
            write.OnCompleted();
            context.Status.OnNext(ConnectionState.Disconnected);
            await dispose;
        }

        (await set).Should().Be(new SettingsResult(SettingsOutcome.Cancelled, 1));
    }
}
