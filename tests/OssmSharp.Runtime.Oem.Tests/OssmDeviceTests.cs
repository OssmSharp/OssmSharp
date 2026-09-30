using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
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

        var set = device.SetAsync(new(StrokePattern.RoboStroke, 20, 30, 40, 50), token);
        var first = await held.Task.WaitAsync(token);

        set.IsCompleted.Should().BeFalse();
        context.RequestsFor("setting.write").Should().ContainSingle();
        context.Respond(first, "completed", result: new JsonObject());
        (await set).Should().Be(new SetResult(SetStatus.Completed, 5));
        var writes = context.RequestsFor("setting.write");
        writes.Select(request => request["path"]!.GetValue<string>())
            .Should().Equal("motion.pattern", "motion.stroke", "motion.depth", "motion.sensation", "motion.speed");
        writes.Select(request => request["args"]!["value"]!.GetValue<int>()).Should().Equal(2, 30, 40, 50, 20);
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
    public async Task StopFollowsIssuedTransferAndSkipsQueuedSettingsWithoutWaitingForEarlierResponse()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write" || request["path"]!.GetValue<string>() != "motion.pattern")
            {
                return null;
            }

            context.Respond(request, "accepted");
            issued.TrySetResult(request);
            return transfer;
        };

        var first = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        var firstRequest = await issued.Task.WaitAsync(token);
        var queued = device.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            stop.IsCompleted.Should().BeFalse();
            context.RequestsFor("setting.write").Should().ContainSingle().Which.Should().BeSameAs(firstRequest);
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
            await stop;
            (await first).Should().Be(new SetResult(SetStatus.Superseded, 0));
            (await queued).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            var writes = context.RequestsFor("setting.write");
            writes.Should().HaveCount(2);
            writes[0]["path"]!.GetValue<string>().Should().Be("motion.pattern");
            writes[1]["path"]!.GetValue<string>().Should().Be("motion.speed");
            writes[1]["args"]!["value"]!.GetValue<double>().Should().Be(0);
            writes[1]["lease"]!.GetValue<uint>().Should().Be(42);
            context.Respond(firstRequest, "completed", result: new JsonObject());
            context.RequestsFor("setting.write").Should().HaveCount(2);
        }
        finally
        {
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
        }
    }

    [Fact]
    public async Task StopTakesGateBeforeQueuedSettingWhileRenewalResponseIsWithheld()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "control.renew")
            {
                return null;
            }

            issued.TrySetResult(request);
            return transfer;
        };

        context.Clock.Advance(TimeSpan.FromSeconds(5));
        var renewal = await issued.Task.WaitAsync(token);
        var setting = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            setting.IsCompleted.Should().BeFalse();
            stop.IsCompleted.Should().BeFalse();
            context.RequestsFor("setting.write").Should().BeEmpty();
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();

            await stop.WaitAsync(token);
            (await setting).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            var writes = context.RequestsFor("setting.write");
            writes.Should().ContainSingle().Which["args"]!["value"]!.GetValue<double>().Should().Be(0);
            context.Requests.Select(BleTestContext.Operation)
                .Should().Equal("control.acquire", "stream.start", "control.renew", "setting.write");
            context.Respond(renewal, "completed", result: new JsonObject { ["ttlMs"] = 10000 });
            context.RequestsFor("setting.write").Should().ContainSingle();
        }
        finally
        {
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
        }
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
            default:
                context.Publish(BleTestContext.InitialMotion, declaredLength: 3);
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

    [Theory]
    [InlineData("{\"homed\":true,\"positionMm\":0,\"speed\":0,\"stroke\":50,\"depth\":10,\"sensation\":50,\"buffer\":100,\"pattern\":0,\"targetPosition\":0,\"targetTimeMs\":0,\"strokeCount\":0,\"")]
    [InlineData("""{"homed":true,"positionMm":0}""")]
    [InlineData("""{"pattern":0,"speed":101,"stroke":50,"depth":10,"sensation":50}""")]
    public async Task InvalidMotionPayloadIsDiscardedAndLiveReadUsesNextObservation(string payload)
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var read = device.GetStateAsync(true, token);

        context.Publish(payload);

        read.IsCompleted.Should().BeFalse();
        device.ConnectionFailure.Should().BeNull();
        device.IsArmed.Should().BeTrue();
        context.Publish(Controls.Replace("{0}", "0", StringComparison.Ordinal));
        (await read.WaitAsync(token)).Should().Be(Initial);
        device.IsArmed.Should().BeTrue();
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
    public async Task OldConnectionStatusAndFramesCannotChangeNewConnection()
    {
        using var context = new BleTestContext();
        using var oldStatus = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        using var newStatus = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        BehaviorSubject<ConnectionState> currentStatus = oldStatus;
        context.Peripheral.Setup(peripheral => peripheral.WhenStatusChanged())
            .Returns(() => currentStatus);
        using var statusEvents = context.Status.Subscribe(state => currentStatus.OnNext(state));
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        await device.DisconnectAsync(token);

        oldStatus.HasObservers.Should().BeFalse();
        currentStatus = newStatus;
        context.MotionStreamId = 8;
        await context.StreamUntilAsync(device.ConnectAsync(token), token);
        newStatus.HasObservers.Should().BeTrue();

        oldStatus.OnNext(ConnectionState.Disconnected);
        context.Publish(Controls.Replace("{0}", "99", StringComparison.Ordinal), streamId: BleTestContext.StreamId);
        context.Publish("{", streamId: BleTestContext.StreamId);

        device.ConnectionFailure.Should().BeNull();
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
        context.RequestsFor("setting.write").Should().ContainSingle().Which["args"]!["value"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task OldGenerationCallbackAlreadyInFlightCannotFaultReconnectedDevice()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var generation = typeof(OssmDevice).GetField("generation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = (long)generation.GetValue(device)!;

        await device.DisconnectAsync(token);
        await context.StreamUntilAsync(device.ConnectAsync(token), token);
        ((long)generation.GetValue(device)!).Should().NotBe(previous);

        var callback = typeof(OssmDevice).GetMethod("ConnectionLost", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(long), typeof(Exception)], modifiers: null)!;
        callback.Invoke(device, [previous, new IOException("Late old-connection failure.")]);

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
        context.DisconnectCalls.Should().Be(1);
    }

    [Fact]
    public async Task LiveObservationPreservesFractionalSpeedWithoutDisarming()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var live = device.GetStateAsync(bypassCache: true, cancellationToken: token);

        context.Publish("""{"pattern":0,"speed":12.5,"stroke":50,"depth":10,"sensation":50}""");

        (await live).Should().Be(new DeviceState(0, 12.5, 50, 10, 50));
        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
    }

    [Fact]
    public async Task AsynchronousObservationUpdatesStateWithoutDisarmingOrIssuingStop()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);

        context.Publish("""{"pattern":0,"speed":0,"stroke":12,"depth":10,"sensation":50,"homed":true}""");

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 12, 10, 50)));
        context.RequestsFor("setting.write").Should().BeEmpty();

        (await device.SetAsync(new(StrokePattern.RoboStroke, 5, 5, 5, 5), token))
            .Should().Be(new SetResult(SetStatus.Completed, 5));

        context.RequestsFor("setting.write").Should().HaveCount(5);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task ModeSelectionDoesNotWaitForObservationBeforeWritingSettings()
    {
        using var context = new BleTestContext
        {
            State = "menu.idle",
            Motion = """{"pattern":0,"speed":0,"stroke":20,"depth":10,"sensation":50,"homed":true}""",
        };
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "target.set")
            {
                return null;
            }

            context.RequestsFor("setting.write").Should().BeEmpty();
            context.State = "strokeEngine.idle";
            context.Motion = Controls.Replace("{0}", "0", StringComparison.Ordinal);
            context.Respond(request, "completed", result: new JsonObject(), stateBefore: "menu.idle");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var set = device.SetAsync(new(StrokePattern.RoboStroke, 5, 5, 5, 5), token);
        try
        {
            context.RequestsFor("setting.write").Should().HaveCount(5);
            (await set).Should().Be(new SetResult(SetStatus.Completed, 5));
            (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 20, 10, 50)));
        }
        finally
        {
            await context.StreamUntilAsync(set, token);
        }

        context.Publish("""{"pattern":2,"speed":1,"stroke":5,"depth":5,"sensation":5,"homed":true}""");

        var target = context.RequestsFor("target.set").Should().ContainSingle().Which;
        target["path"]!.GetValue<string>().Should().Be("target.strokeEngine");
        target["lease"]!.GetValue<uint>().Should().Be(42);
        context.RequestsFor("setting.write").Should().HaveCount(5);
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(StrokePattern.RoboStroke, 1, 5, 5, 5)));
    }

    [Fact]
    public async Task SetDuringPreflightSendsNoSettings()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        context.State = "strokeEngine.preflight";

        await device.Invoking(value => value.SetAsync(new(0, 5, 5, 5, 5), token))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*speed knob*");

        context.RequestsFor("target.set").Should().ContainSingle();
        context.RequestsFor("setting.write").Should().BeEmpty();
        device.IsArmed.Should().BeTrue();
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
    public async Task MalformedResponseIsDiscardedAndRequestIsResentWithItsIdentifier()
    {
        using var context = new BleTestContext();
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var first = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resent = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write")
            {
                return null;
            }

            if (first.TrySetResult(request))
            {
                context.SendResponse("""{,"result":{"path":"motion.pattern","value":1}}"""u8.ToArray());
                return Observable.Return(BleTestContext.WriteResult([]));
            }

            resent.TrySetResult(request);
            return null;
        };

        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var original = await first.Task.WaitAsync(token);

        device.ConnectionFailure.Should().BeNull();
        set.IsCompleted.Should().BeFalse();
        for (var elapsed = 0; elapsed < 10 && !resent.Task.IsCompleted; elapsed++)
        {
            context.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, token);
        }

        resent.Task.IsCompleted.Should().BeTrue();
        (await resent.Task)["id"]!.GetValue<uint>().Should().Be(original["id"]!.GetValue<uint>());
        (await set.WaitAsync(token)).Should().Be(new SetResult(SetStatus.Completed, 5));
        device.IsArmed.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 2000)]
    [InlineData(1, 250)]
    [InlineData(2, 250)]
    public async Task UnansweredRequestFailsConnectionAfterBoundedResends(int? maximumRequestAttempts, int responseTimeoutMilliseconds)
    {
        var responseTimeout = TimeSpan.FromMilliseconds(responseTimeoutMilliseconds);
        using var context = new BleTestContext
        {
            DeviceOptions = maximumRequestAttempts is { } attempts
                ? new() { MaximumRequestAttempts = attempts, ResponseTimeout = responseTimeout }
                : null,
        };
        var token = TestContext.Current.CancellationToken;
        await using var device = await context.ConnectAsync(token);
        var failed = WhenConnectionFails(device);
        context.Write = request => BleTestContext.Operation(request) == "setting.write"
            ? Observable.Return(BleTestContext.WriteResult([]))
            : null;

        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        for (var elapsed = 0; elapsed < 10 && !failed.IsCompleted; elapsed++)
        {
            context.Clock.Advance(responseTimeout);
            await Task.Delay(1, token);
        }

        context.Write = null;
        failed.IsCompleted.Should().BeTrue();
        device.ConnectionFailure.Should().BeOfType<IOException>().Which.Message.Should().Contain("did not answer");
        device.IsArmed.Should().BeFalse();
        var writes = context.RequestsFor("setting.write");
        writes.Should().HaveCount(maximumRequestAttempts ?? 3);
        writes.Select(request => request["id"]!.GetValue<uint>()).Distinct().Should().ContainSingle();
        var result = () => set;
        await result.Should().ThrowAsync<DeviceWriteException>();
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

        context.Requests.Select(BleTestContext.Operation).Should().EndWith(["setting.write", "setting.write", "stream.stop", "control.release"]);
        var stop = context.RequestsFor("setting.write")[5];
        stop["path"]!.GetValue<string>().Should().Be("motion.speed");
        stop["args"]!["value"]!.GetValue<double>().Should().Be(0);
        stop["lease"]!.GetValue<uint>().Should().Be(42);
        context.RequestsFor("control.release").Should().ContainSingle().Which["lease"]!.GetValue<uint>().Should().Be(42);
        context.DisconnectCalls.Should().Be(1);
        context.Responses.HasObservers.Should().BeFalse();
        context.Frames.HasObservers.Should().BeFalse();
        device.IsArmed.Should().BeFalse();
        device.ConnectionFailure.Should().BeNull();

        context.Lease = 43;
        await context.StreamUntilAsync(device.ConnectAsync(token), token);

        device.IsArmed.Should().BeTrue();
        context.RequestsFor("setting.write").Should().HaveCount(6);
        context.RequestsFor("control.acquire").Should().HaveCount(2);
        context.RequestsFor("stream.start")[1]["lease"]!.GetValue<uint>().Should().Be(43);
    }

    [Fact]
    public async Task FailedStopDuringDisconnectStillReleasesLeaseAndAllowsReconnect()
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

            context.Respond(request, "failed", ok: false, code: "stop_rejected");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var disconnect = () => device.DisconnectAsync(token);
        await disconnect.Should().ThrowAsync<IOException>().WithMessage("*stop_rejected*");

        context.Requests.Select(BleTestContext.Operation).Should().EndWith(["setting.write", "stream.stop", "control.release"]);
        context.RequestsFor("control.release").Should().ContainSingle().Which["lease"]!.GetValue<uint>().Should().Be(42);
        context.DisconnectCalls.Should().Be(1);
        context.Responses.HasObservers.Should().BeFalse();
        context.Frames.HasObservers.Should().BeFalse();

        context.Write = null;
        context.Lease = 43;
        await context.StreamUntilAsync(device.ConnectAsync(token), token);

        device.IsArmed.Should().BeTrue();
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
    public async Task DisposeAwaitsIssuedTransferAndDisconnectAcknowledgement()
    {
        using var context = new BleTestContext { CompleteDisconnect = false };
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "setting.write"
                || request["path"]!.GetValue<string>() == "motion.speed" && request["args"]!["value"]!.GetValue<double>() == 0)
            {
                return null;
            }

            issued.TrySetResult(request);
            return transfer;
        };
        var set = device.SetAsync(new(0, 20, 30, 40, 50), token);
        var request = await issued.Task.WaitAsync(token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            dispose.IsCompleted.Should().BeFalse();
            context.RequestsFor("setting.write").Should().ContainSingle();
            context.RequestsFor("control.release").Should().BeEmpty();
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
            (await set).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            await context.ConnectionCancelled.WaitAsync(token);
            dispose.IsCompleted.Should().BeFalse();
        }
        finally
        {
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
            context.Status.OnNext(ConnectionState.Disconnected);
            await dispose;
        }

        context.Respond(request, "completed", result: new JsonObject());
        context.Publish("{");
        context.Responses.HasObservers.Should().BeFalse();
        context.Frames.HasObservers.Should().BeFalse();
        context.Requests.Select(BleTestContext.Operation)
            .Should().Equal("control.acquire", "stream.start", "target.set", "setting.write", "setting.write", "stream.stop", "control.release");
        context.RequestsFor("setting.write").Should().HaveCount(2);
        device.ConnectionFailure.Should().BeNull();
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
