using System.Reactive.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceTests
{
    private const string Controls = """{"pattern":0,"speed":{0},"stroke":50,"depth":10,"sensation":50,"homed":true}""";
    private static readonly DeviceState Initial = new(new(0, 0, 50, 10, 50));

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
    public async Task ConnectValidatesProtocolLeasesAndStreamsThenArmsWithoutWritingSettings()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;

        await using var device = await context.ConnectAsync(token);

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        context.ConnectionConfiguration.Should().Be(new ConnectionConfig(false));
        context.Requests.Select(BleTestContext.Operation).Should().Equal("control.acquire", "stream.start");
        context.Requests.Select(request => request["v"]!.GetValue<int>()).Should().AllBeEquivalentTo(1);
        context.Requests.Select(request => request["id"]!.GetValue<uint>()).Should().OnlyHaveUniqueItems().And.NotContain(0u);
        context.Requests[0].ContainsKey("lease").Should().BeFalse();
        context.Requests[1]["path"]!.GetValue<string>().Should().Be("motion.speed");
        context.Requests[1]["lease"]!.GetValue<uint>().Should().Be(42);
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
    }

    [Fact]
    public async Task WaitsForSubscriptionsBeforeIssuingRequests()
    {
        using var context = new BleTestContext { CompleteSubscription = false };
        await using var device = context.CreateDevice();
        var token = TestContext.Current.CancellationToken;
        var connect = device.ConnectAsync(token);

        try
        {
            connect.IsCompleted.Should().BeFalse();
            context.Requests.Should().BeEmpty();
        }
        finally
        {
            context.AcknowledgeSubscriptions();
            await context.StreamUntilAsync(connect, token);
        }

        context.Requests.Select(BleTestContext.Operation).Should().Equal("control.acquire", "stream.start");
    }

    [Theory]
    [InlineData("""{"protocol":"rad-ble","version":2}""")]
    [InlineData("""{"protocol":"other","version":1}""")]
    [InlineData("""{"protocol":"rad-ble","version":"1"}""")]
    [InlineData("not json")]
    public async Task UnsupportedProtocolSendsNoRequestsAndReleasesConnection(string info)
    {
        using var context = new BleTestContext { ProtocolInfo = Encoding.UTF8.GetBytes(info) };
        await using var device = context.CreateDevice();

        var connect = () => device.ConnectAsync(TestContext.Current.CancellationToken);

        await connect.Should().ThrowAsync<InvalidDataException>().WithMessage("*RAD BLE*");
        context.Requests.Should().BeEmpty();
        context.DisconnectCalls.Should().Be(1);
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task SettingsSendLeasedWritesCompletedOnlyByTerminalResponses()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var held = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write" || held.Task.IsCompleted)
            {
                return null;
            }

            context.Respond(request, "accepted");
            held.TrySetResult(request);
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var set = device.SetAsync(new(StrokePattern.RoboStroke, 20.5, 30, 40, 50), token);
        var first = await held.Task.WaitAsync(token);

        set.IsCompleted.Should().BeFalse();
        context.RequestsFor("setting.write").Should().ContainSingle();
        context.Respond(first, "completed", result: new JsonObject());
        (await set).Should().Be(new SetResult(SetStatus.Completed, 5));
        var writes = context.RequestsFor("setting.write");
        writes.Select(request => request["path"]!.GetValue<string>())
            .Should().Equal("motion.pattern", "motion.stroke", "motion.depth", "motion.sensation", "motion.speed");
        writes.Select(request => request["args"]!["value"]!.GetValue<double>()).Should().Equal(2, 30, 40, 50, 20.5);
        writes.Should().OnlyContain(request => request["lease"]!.GetValue<uint>() == 42);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task RejectedWritePreservesPartialProgressAndFailureCodeWithoutRetry()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write" || context.RequestsFor("setting.write").Count != 2)
            {
                return null;
            }

            context.Respond(request, "accepted");
            context.Respond(request, "failed", ok: false, code: "invalid_value");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var set = () => device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var error = (await set.Should().ThrowAsync<DeviceWriteException>()).Which;

        error.CommandsWritten.Should().Be(1);
        error.Command.Should().Be(new DeviceCommand(DeviceControl.Stroke, 30));
        error.InnerException.Should().BeOfType<IOException>().Which.Message.Should().Contain("invalid_value");
        context.RequestsFor("setting.write").Should().HaveCount(2);
        device.IsArmed.Should().BeFalse();
        device.ConnectionFailure.Should().BeNull();
    }

    [Fact]
    public async Task StopSendsLeasedSpeedZeroAndDisarms()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);

        await device.EmergencyStopAsync(token);

        var stop = context.RequestsFor("setting.write").Should().ContainSingle().Which;
        stop["path"]!.GetValue<string>().Should().Be("motion.speed");
        stop["args"]!["value"]!.GetValue<double>().Should().Be(0);
        stop["lease"]!.GetValue<uint>().Should().Be(42);
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task LiveReadWaitsForTheNextMotionFrame()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);

        var read = device.GetStateAsync(true, token);

        read.IsCompleted.Should().BeFalse();
        (await context.StreamUntilAsync(read, token)).Should().Be(Initial);
        device.IsArmed.Should().BeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("version")]
    [InlineData("binary")]
    [InlineData("length")]
    [InlineData("range")]
    [InlineData("json")]
    public async Task MalformedFrameFailsConnectionAndLiveReadWithoutCachedSuccess(string defect)
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var read = device.GetStateAsync(true, token);

        switch (defect)
        {
            case "missing":
                context.PublishFrame(null);
                break;
            case "short":
                context.PublishFrame(new byte[20]);
                break;
            case "version":
                context.Publish(BleTestContext.InitialMotion, version: 2);
                break;
            case "binary":
                context.Publish(BleTestContext.InitialMotion, flags: 0);
                break;
            case "length":
                context.Publish(BleTestContext.InitialMotion, declaredLength: 3);
                break;
            case "range":
                context.Publish(Controls.Replace("{0}", "101", StringComparison.Ordinal));
                break;
            default:
                context.Publish("{");
                break;
        }

        var live = () => read;
        await live.Should().ThrowAsync<InvalidDataException>();
        device.ConnectionFailure.Should().BeOfType<InvalidDataException>();
        device.IsArmed.Should().BeFalse();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
        var reset = () => device.ResetAsync(token);
        await reset.Should().ThrowAsync<InvalidDataException>();
        context.RequestsFor("setting.write").Should().BeEmpty();
    }

    [Fact]
    public async Task FramesFromOtherStreamsAreIgnored()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);

        context.Publish(Controls.Replace("{0}", "99", StringComparison.Ordinal), streamId: 3);
        context.Publish("{", streamId: 3);

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
    }

    [Fact]
    public async Task AsynchronousObservationUpdatesStateAndUnexplainedChangeDisarms()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);

        context.Publish(Controls.Replace("{0}", "12.5", StringComparison.Ordinal));

        device.IsArmed.Should().BeFalse();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 12.5, 50, 10, 50)));
    }

    [Fact]
    public async Task ObservedOwnWritesKeepSubmissionArmed()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        await device.SetAsync(new(0, 20, 50, 10, 50), token);

        context.Publish(Controls.Replace("{0}", "20", StringComparison.Ordinal));

        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 20, 50, 10, 50)));
    }

    [Fact]
    public async Task MalformedResponseFailsConnectionAndPendingWrite()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write")
            {
                return null;
            }

            context.SendResponse("""{"v":1,"stage":"completed","ok":true}"""u8.ToArray());
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var set = () => device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);

        (await set.Should().ThrowAsync<DeviceWriteException>()).Which.InnerException.Should().BeOfType<InvalidDataException>();
        device.ConnectionFailure.Should().BeOfType<InvalidDataException>();
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task ResponsesForUnknownRequestsAreIgnored()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);

        context.Respond(new JsonObject { ["id"] = 999u }, "completed");
        context.Respond(new JsonObject { ["id"] = 0u }, "failed", ok: false, code: "invalid_request");

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
    }

    [Fact]
    public async Task RenewsLeaseAtHalfItsReportedLifetime()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var renewed = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) == "control.renew")
            {
                renewed.TrySetResult(request);
            }

            return null;
        };

        context.Clock.Advance(TimeSpan.FromSeconds(4.9));
        context.RequestsFor("control.renew").Should().BeEmpty();
        context.Clock.Advance(TimeSpan.FromSeconds(0.1));
        var renewal = await renewed.Task.WaitAsync(token);

        renewal["lease"]!.GetValue<uint>().Should().Be(42);
        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
    }

    [Fact]
    public async Task RejectedRenewalFailsConnectionWithoutReacquiringTheLease()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var failed = WhenConnectionFails(device);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "control.renew")
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: "lease_required");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        context.Clock.Advance(TimeSpan.FromSeconds(5));
        await failed.WaitAsync(token);

        device.ConnectionFailure.Should().BeOfType<IOException>().Which.Message.Should().Contain("control ownership is lost");
        device.IsArmed.Should().BeFalse();
        context.RequestsFor("control.acquire").Should().ContainSingle();
        var set = () => device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        await set.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task UnansweredRenewalFailsConnectionAtLeaseExpiry()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var failed = WhenConnectionFails(device);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "control.renew")
            {
                return null;
            }

            renewed.TrySetResult();
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        context.Clock.Advance(TimeSpan.FromSeconds(5));
        await renewed.Task.WaitAsync(token);
        device.ConnectionFailure.Should().BeNull();
        context.Clock.Advance(TimeSpan.FromSeconds(5));
        await failed.WaitAsync(token);

        device.ConnectionFailure.Should().BeOfType<IOException>().Which.Message.Should().Contain("expired");
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectReleasesLeaseAndReconnectUsesFreshLeaseWithoutReplayingSettings()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        await device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);

        await device.DisconnectAsync(token);

        context.Requests.Select(BleTestContext.Operation).Should().EndWith(["setting.write", "stream.stop", "control.release"]);
        context.RequestsFor("control.release").Should().ContainSingle().Which["lease"]!.GetValue<uint>().Should().Be(42);
        context.DisconnectCalls.Should().Be(1);
        context.Responses.HasObservers.Should().BeFalse();
        context.Frames.HasObservers.Should().BeFalse();
        device.IsArmed.Should().BeFalse();
        device.ConnectionFailure.Should().BeNull();

        context.Lease = 43;
        await context.StreamUntilAsync(device.ConnectAsync(token), token);

        device.IsArmed.Should().BeTrue();
        context.RequestsFor("setting.write").Should().HaveCount(5);
        context.RequestsFor("control.acquire").Should().HaveCount(2);
        context.RequestsFor("stream.start")[1]["lease"]!.GetValue<uint>().Should().Be(43);
    }

    [Fact]
    public async Task ConnectionLossFailsPendingWriteAndClientCanReconnect()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write")
            {
                return null;
            }

            issued.TrySetResult();
            return Observable.Return(BleTestContext.WriteResult([]));
        };
        var pending = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        await issued.Task.WaitAsync(token);

        context.Status.OnNext(ConnectionState.Disconnected);

        var result = () => pending;
        (await result.Should().ThrowAsync<DeviceWriteException>()).Which.CommandsWritten.Should().Be(0);
        device.ConnectionFailure.Should().BeOfType<IOException>();
        device.IsArmed.Should().BeFalse();
        var set = () => device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        await set.Should().ThrowAsync<IOException>();

        context.Write = null;
        await context.StreamUntilAsync(device.ConnectAsync(token), token);

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        context.DisconnectCalls.Should().Be(1);
        context.RequestsFor("control.release").Should().BeEmpty();
        context.RequestsFor("setting.write").Should().ContainSingle();
    }

    [Fact]
    public async Task DisposeAwaitsIssuedWriteResponseAndDisconnectAcknowledgement()
    {
        using var context = new BleTestContext { CompleteDisconnect = false };
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(token);
        var issued = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write")
            {
                return null;
            }

            issued.TrySetResult(request);
            return Observable.Return(BleTestContext.WriteResult([]));
        };
        var set = device.SetAsync(new(0, 20, 30, 40, 50), token);
        var request = await issued.Task.WaitAsync(token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            dispose.IsCompleted.Should().BeFalse();
            context.RequestsFor("control.release").Should().BeEmpty();
            context.Write = null;
            context.Respond(request, "completed", result: new JsonObject());
            (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
            await context.ConnectionCancelled.WaitAsync(token);
            dispose.IsCompleted.Should().BeFalse();
        }
        finally
        {
            context.Status.OnNext(ConnectionState.Disconnected);
            await dispose;
        }

        context.RequestsFor("stream.stop").Should().ContainSingle();
        context.RequestsFor("control.release").Should().ContainSingle();
        context.DisconnectCalls.Should().Be(1);
    }

    [Fact]
    public async Task DisposeSendsLeasedStopBeforeReleasingControl()
    {
        using var context = new BleTestContext();
        var device = await context.ConnectAsync(TestContext.Current.CancellationToken);

        await device.DisposeAsync();

        context.Requests.Select(BleTestContext.Operation)
            .Should().Equal("control.acquire", "stream.start", "setting.write", "stream.stop", "control.release");
        var stop = context.RequestsFor("setting.write").Should().ContainSingle().Which;
        stop["path"]!.GetValue<string>().Should().Be("motion.speed");
        stop["args"]!["value"]!.GetValue<double>().Should().Be(0);
        stop["lease"]!.GetValue<uint>().Should().Be(42);
    }

    [Fact]
    public async Task DisposeAfterConnectionLossSendsNoStop()
    {
        using var context = new BleTestContext();
        var device = await context.ConnectAsync(TestContext.Current.CancellationToken);
        context.Status.OnNext(ConnectionState.Disconnected);

        await device.DisposeAsync();

        context.RequestsFor("setting.write").Should().BeEmpty();
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
        context.Requests.Should().BeEmpty();
        context.Status.Value.Should().Be(ConnectionState.Disconnected);
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
            await context.StreamUntilAsync(firstConnect, TestContext.Current.CancellationToken);
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
            context.ResponseSubscriptionChanges.OnNext(BleTestContext.Characteristic(BleProtocolConstants.Response) with { IsNotifying = false });
            var result = () => connect;
            await result.Should().ThrowAsync<IOException>();
            context.NotificationDisposals.Should().Be(1);
            context.DisconnectCalls.Should().Be(1);
            context.Requests.Should().BeEmpty();
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

    private static Task WhenConnectionFails(IOssmDevice device)
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.StateChanged += (_, _) =>
        {
            if (device.ConnectionFailure is not null)
            {
                failed.TrySetResult();
            }
        };
        return failed.Task;
    }
}
