using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using FastMoq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public abstract class OssmDeviceTestBase : MockerTestBase<OssmDevice>, IAsyncDisposable
{
    protected const string Controls = """{"pattern":0,"speed":{0},"stroke":50,"depth":10,"sensation":50,"homed":true}""";
    protected static readonly DeviceState Initial = new(new(0, 0, 50, 10, 50));
    private protected BleTestContext context = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        context = new(mocks);
        mocks.AddType<string>("device-1");
        mocks.AddType<ILogger<DeviceBase>>(context.Logger);
        mocks.AddType<IOptions<OssmDeviceOptions>>(Options.Create(context.DeviceOptions));
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            try
            {
                context.Dispose();
            }
            finally
            {
                try
                {
                    base.Dispose();
                }
                finally
                {
                    GC.SuppressFinalize(this);
                }
            }
        }
    }

    protected static Task WhenConnectionFails(IOssmDevice device)
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

public sealed class OssmDeviceTests : OssmDeviceTestBase
{
    [Fact]
    public async Task SetupFailureRetainsOwnershipUntilDisconnectAcknowledged()
    {
        context.CompleteDisconnect = false;
        var failure = new IOException("Connection setup failed after issuance.");
        var nativeState = ConnectionState.Disconnected;
        context.Peripheral.SetupGet(peripheral => peripheral.Status).Returns(() => nativeState);
        context.Peripheral.Setup(peripheral => peripheral.WhenStatusChanged()).Returns(context.Status.DistinctUntilChanged());
        context.Peripheral.Setup(peripheral => peripheral.Connect(Moq.It.IsAny<ConnectionConfig>())).Callback(() =>
        {
            nativeState = ConnectionState.Connecting;
            throw failure;
        });
        var device = Component;
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
        var token = TestContext.Current.CancellationToken;

        var device = await context.ConnectAsync(Component, token);

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
    public async Task ConnectHelperFailurePreservesOriginalErrorAndCallerOwnership()
    {
        var failure = new IOException("Protocol read failed.");
        context.Peripheral.Setup(peripheral => peripheral.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo))
            .Returns(Observable.Throw<BleCharacteristicResult>(failure));

        var error = await Assert.ThrowsAsync<IOException>(
            () => context.ConnectAsync(Component, TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.False(Component.IsArmed);
        Assert.Empty(context.Requests);
        await Component.DisposeAsync();
        Assert.False(context.Status.HasObservers);
        Assert.False(context.ConnectionFailures.HasObservers);
        Assert.Equal(1, context.DisconnectCalls);
    }

    [Fact]
    public async Task WaitsForSubscriptionsBeforeIssuingRequests()
    {
        context.CompleteSubscription = false;
        var device = Component;
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
        context.ProtocolInfo = Encoding.UTF8.GetBytes(info);
        var device = Component;

        var connect = () => device.ConnectAsync(TestContext.Current.CancellationToken);

        await connect.Should().ThrowAsync<InvalidDataException>().WithMessage("*RAD BLE*");
        context.Requests.Should().BeEmpty();
        context.DisconnectCalls.Should().Be(1);
        device.IsArmed.Should().BeFalse();
    }

    [Theory]
    [InlineData(StrokePattern.RoboStroke, 20, 30, 40, 50, 6)]
    [InlineData(StrokePattern.SimpleStroke, 5, 5, 5, 5, 0)]
    [InlineData(StrokePattern.SimpleStroke, 53, 10, 0, 100, 5)]
    [InlineData(StrokePattern.SimpleStroke, 100, 0, 100, 0, 0)]
    [InlineData(StrokePattern.SimpleStroke, 0, 100, 0, 0, 0)]
    public async Task SettingsSendLeasedWritesCompletedOnlyByTerminalResponses(StrokePattern pattern, int speed, int stroke, int depth, int sensation, int expectedDeviceVelocityPercent)
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var held = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request) || held.Task.IsCompleted)
            {
                return null;
            }

            context.Respond(request, "accepted");
            held.TrySetResult(request);
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var set = device.SetAsync(new(pattern, speed, stroke, depth, sensation), token);
        var first = await held.Task.WaitAsync(token);

        set.IsCompleted.Should().BeFalse();
        context.RequestsFor("setting.write", "motion.").Should().ContainSingle();
        context.Respond(first, "completed", result: new JsonObject());
        (await set).Should().Be(new SetResult(SetStatus.Completed, 5));
        var writes = context.RequestsFor("setting.write", "motion.");
        writes.Select(request => request["path"]!.GetValue<string>())
            .Should().Equal("motion.pattern", "motion.stroke", "motion.depth", "motion.sensation", "motion.speed");
        writes.Select(request => request["args"]!["value"]!.GetValue<int>()).Should().Equal((int)pattern, stroke, depth, sensation, expectedDeviceVelocityPercent);
        writes.Should().OnlyContain(request => request["lease"]!.GetValue<uint>() == 42);
        Assert.Equal(new FirmwareSettings((int)pattern, expectedDeviceVelocityPercent, stroke, depth, sensation), device.Transmitted);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task RejectedWritePreservesPartialProgressAndFailureCodeWithoutRetry()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request) || context.RequestsFor("setting.write", "motion.").Count != 2)
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
        error.Command.Should().Be(new FirmwareCommand(FirmwareSetting.Stroke, 30));
        error.InnerException.Should().BeOfType<IOException>().Which.Message.Should().Contain("invalid_value");
        context.RequestsFor("setting.write", "motion.").Should().HaveCount(2);
        device.IsArmed.Should().BeFalse();
        device.ConnectionFailure.Should().BeNull();
    }

    [Fact]
    public async Task StopSendsLeasedSpeedZeroAndPreservesArming()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);

        await device.EmergencyStopAsync(token);

        var stop = context.RequestsFor("setting.write", "motion.").Should().ContainSingle().Which;
        stop["path"]!.GetValue<string>().Should().Be("motion.speed");
        stop["args"]!["value"]!.GetValue<double>().Should().Be(0);
        stop["lease"]!.GetValue<uint>().Should().Be(42);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task StopFollowsIssuedTransferAndSkipsQueuedSettingsWithoutWaitingForEarlierResponse()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request) || request["path"]!.GetValue<string>() != "motion.pattern")
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
            context.RequestsFor("setting.write", "motion.").Should().ContainSingle().Which.Should().BeSameAs(firstRequest);
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
            await stop;
            (await first).Should().Be(new SetResult(SetStatus.Superseded, 0));
            (await queued).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            var writes = context.RequestsFor("setting.write", "motion.");
            writes.Should().HaveCount(2);
            writes[0]["path"]!.GetValue<string>().Should().Be("motion.pattern");
            writes[1]["path"]!.GetValue<string>().Should().Be("motion.speed");
            writes[1]["args"]!["value"]!.GetValue<double>().Should().Be(0);
            writes[1]["lease"]!.GetValue<uint>().Should().Be(42);
            context.Respond(firstRequest, "completed", result: new JsonObject());
            context.RequestsFor("setting.write", "motion.").Should().HaveCount(2);
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
            context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();

            await stop.WaitAsync(token);
            (await setting).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            var writes = context.RequestsFor("setting.write", "motion.");
            writes.Should().ContainSingle().Which["args"]!["value"]!.GetValue<double>().Should().Be(0);
            context.Requests.Select(BleTestContext.Operation)
                .Should().Equal("control.acquire", "stream.start", "control.renew", "setting.write");
            context.Respond(renewal, "completed", result: new JsonObject { ["ttlMs"] = 10000 });
            context.RequestsFor("setting.write", "motion.").Should().ContainSingle();
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);

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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
        context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"homed\":true,\"positionMm\":0,\"speed\":0,\"stroke\":50,\"depth\":10,\"sensation\":50,\"buffer\":100,\"pattern\":0,\"targetPosition\":0,\"targetTimeMs\":0,\"strokeCount\":0,\"")]
    [InlineData("""{"homed":true,"positionMm":0}""")]
    [InlineData("""{"pattern":0,"speed":101,"stroke":50,"depth":10,"sensation":50}""")]
    public async Task InvalidMotionPayloadIsDiscardedAndLiveReadUsesNextObservation(string payload)
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);

        context.Publish(Controls.Replace("{0}", "99", StringComparison.Ordinal), streamId: 3);
        context.Publish("{", streamId: 3);

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
    }

    [Fact]
    public async Task OldConnectionStatusAndFramesCannotChangeNewConnection()
    {
        using var oldStatus = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        using var newStatus = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        BehaviorSubject<ConnectionState> currentStatus = oldStatus;
        context.Peripheral.Setup(peripheral => peripheral.WhenStatusChanged())
            .Returns(() => currentStatus);
        using var statusEvents = context.Status.Subscribe(state => currentStatus.OnNext(state));
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
        context.RequestsFor("setting.write", "motion.").Should().ContainSingle().Which["args"]!["value"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task OldGenerationCallbackAlreadyInFlightCannotFaultReconnectedDevice()
    {
        var observers = new List<IObserver<BleException>>();
        var disposed = new HashSet<IObserver<BleException>>();
        var failures = Mocks.GetOrCreateMock<IObservable<BleException>>().Instance;
        Mock.Get(failures).Setup(source => source.Subscribe(It.IsAny<IObserver<BleException>>()))
            .Returns((IObserver<BleException> observer) =>
            {
                observers.Add(observer);
                return Disposable.Create(() => disposed.Add(observer));
            });
        context.Peripheral.Setup(peripheral => peripheral.WhenConnectionFailed()).Returns(failures);
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var oldObserver = observers[0];
        var previousCount = observers.Count;
        Action oldCallback = () => oldObserver.OnError(new IOException("Late old-connection failure."));

        await device.DisconnectAsync(token);
        Assert.Contains(oldObserver, disposed);
        await context.StreamUntilAsync(device.ConnectAsync(token), token);
        Assert.True(observers.Count > previousCount);
        oldCallback();

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(Initial);
        context.DisconnectCalls.Should().Be(1);

        var currentFailure = new IOException("Current connection failure.");
        observers[previousCount].OnError(currentFailure);
        Assert.False(device.IsArmed);
        Assert.Same(currentFailure, device.ConnectionFailure);
    }

    [Fact]
    public async Task LiveObservationPreservesFractionalSpeedWithoutDisarming()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var live = device.GetStateAsync(bypassCache: true, cancellationToken: token);

        context.Publish("""{"pattern":0,"speed":12.5,"stroke":50,"depth":10,"sensation":50}""");

        var state = await live;
        Assert.Equal(25, state?.Speed);
        Assert.Equal(new DeviceState(0, 12.5, 50, 10, 50), state);
        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
    }

    [Fact]
    public async Task PendingSettingsDoNotDetermineObservedSpeed()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var held = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request => BleTestContext.IsMotionWrite(request) && held.TrySetResult(request)
            ? Observable.Return(BleTestContext.WriteResult([])) : null;
        var set = device.SetAsync(new(StrokePattern.RoboStroke, 53, 80, 0, 100), token);
        var first = await held.Task.WaitAsync(token);
        try
        {
            Assert.False(set.IsCompleted);
            context.Publish("""{"pattern":0,"speed":12.5,"stroke":25,"depth":99,"sensation":50}""");

            var observed = await device.GetStateAsync(cancellationToken: token);
            Assert.Equal(50, observed?.Speed);
            Assert.Equal(25, observed?.Stroke);
            Assert.Null(device.Transmitted);
            Assert.True(device.IsArmed);
        }
        finally
        {
            context.Complete(first);
            await set;
        }

        Assert.Equal(new FirmwareSettings(2, 42, 80, 0, 100), device.Transmitted);
        Assert.Equal(50, (await device.GetStateAsync(cancellationToken: token))?.Speed);
    }

    [Fact]
    public async Task AsynchronousObservationUpdatesStateWithoutDisarmingOrIssuingStop()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);

        context.Publish("""{"pattern":0,"speed":0,"stroke":12,"depth":10,"sensation":50,"homed":true}""");

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 12, 10, 50)));
        context.RequestsFor("setting.write", "motion.").Should().BeEmpty();

        (await device.SetAsync(new(StrokePattern.RoboStroke, 5, 5, 5, 5), token))
            .Should().Be(new SetResult(SetStatus.Completed, 5));

        context.RequestsFor("setting.write", "motion.").Should().HaveCount(5);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task ModeSelectionDoesNotWaitForObservationBeforeWritingSettings()
    {
        context.State = "menu.idle";
        context.Motion = """{"pattern":0,"speed":0,"stroke":20,"depth":10,"sensation":50,"homed":true}""";
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "target.set")
            {
                return null;
            }

            context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
            context.State = "strokeEngine.idle";
            context.Motion = Controls.Replace("{0}", "0", StringComparison.Ordinal);
            context.Respond(request, "completed", result: new JsonObject(), stateBefore: "menu.idle");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var set = device.SetAsync(new(StrokePattern.RoboStroke, 5, 5, 5, 5), token);
        try
        {
            context.RequestsFor("setting.write", "motion.").Should().HaveCount(5);
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
        context.RequestsFor("setting.write", "motion.").Should().HaveCount(5);
        device.IsArmed.Should().BeTrue();
        var observed = await device.GetStateAsync(cancellationToken: token);
        Assert.Equal(new DeviceState(StrokePattern.RoboStroke, 1, 5, 5, 5), observed);
        Assert.Equal(20, observed?.Speed);
    }

    [Fact]
    public async Task SetWaitsForDirectSpeedSetupBeforeSelectingModeOrWritingMotion()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        context.Write = request => request["path"]?.GetValue<string>() == "setting.speedKnobAsLimit"
            ? Observable.Return(BleTestContext.WriteResult([]))
            : null;

        var set = device.SetAsync(new(StrokePattern.SimpleStroke, 5, 5, 5, 5), token);
        try
        {
            var setup = context.RequestsFor("setting.write").Should().ContainSingle().Which;
            setup["path"]!.GetValue<string>().Should().Be("setting.speedKnobAsLimit");
            setup["args"]!["value"]!.GetValue<bool>().Should().BeFalse();
            setup["lease"]!.GetValue<uint>().Should().Be(42);
            context.RequestsFor("target.set").Should().BeEmpty();
            set.IsCompleted.Should().BeFalse();

            context.Complete(setup);
            (await set).Should().Be(new SetResult(SetStatus.Completed, 5));
            context.Requests.Skip(2).Select(request => request["path"]!.GetValue<string>())
                .Should().Equal("setting.speedKnobAsLimit", "target.strokeEngine", "motion.pattern", "motion.stroke", "motion.depth", "motion.sensation", "motion.speed");
        }
        finally
        {
            context.Write = null;
            await device.EmergencyStopAsync(token);
            await set;
        }
    }

    [Fact]
    public async Task RejectedDirectSpeedSetupDisarmsWithoutSelectingModeOrWritingMotion()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (request["path"]?.GetValue<string>() != "setting.speedKnobAsLimit")
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: "invalid_value");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        await device.Invoking(value => value.SetAsync(new(0, 5, 5, 5, 5), token))
            .Should().ThrowAsync<IOException>().WithMessage("*no settings were sent*invalid_value*");

        device.IsArmed.Should().BeFalse();
        context.RequestsFor("target.set").Should().BeEmpty();
        context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
        context.RequestsFor("setting.write").Should().ContainSingle();
    }

    [Fact]
    public async Task CancelledDirectSpeedSetupDoesNotWriteMotionAfterLateAcknowledgement()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        context.Write = request => request["path"]?.GetValue<string>() == "setting.speedKnobAsLimit"
            ? Observable.Return(BleTestContext.WriteResult([]))
            : null;

        var set = device.SetAsync(new(0, 5, 5, 5, 5), cancellation.Token);
        var setup = context.RequestsFor("setting.write").Should().ContainSingle().Which;
        await cancellation.CancelAsync();

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 0));
        context.Complete(setup);
        context.RequestsFor("target.set").Should().BeEmpty();
        context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
    }

    [Fact]
    public async Task SetDuringPreflightSendsNoSettings()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        context.State = "strokeEngine.preflight";

        await device.Invoking(value => value.SetAsync(new(0, 5, 5, 5, 5), token))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*speed knob*");

        context.RequestsFor("target.set").Should().ContainSingle();
        context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task ObservedOwnWritesKeepRequestArmed()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        await device.SetAsync(new(0, 20, 50, 10, 50), token);

        context.Publish(Controls.Replace("{0}", "10", StringComparison.Ordinal));

        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 20, 50, 10, 50)));
    }

}

public sealed class OssmDeviceRequestTests : OssmDeviceTestBase
{
    [Fact]
    public async Task MalformedResponseIsDiscardedAndRequestIsResentWithItsIdentifier()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var first = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resent = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.TimerCreated = dueTime =>
        {
            if (dueTime == context.DeviceOptions.ResponseTimeout)
            {
                timerRegistered.TrySetResult();
            }
        };
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request))
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
        try
        {
            var original = await first.Task.WaitAsync(token);
            await timerRegistered.Task.WaitAsync(token);
            device.ConnectionFailure.Should().BeNull();
            set.IsCompleted.Should().BeFalse();
            resent.Task.IsCompleted.Should().BeFalse();

            context.Clock.Advance(context.DeviceOptions.ResponseTimeout);

            var retry = await resent.Task.WaitAsync(token);
            retry["id"]!.GetValue<uint>().Should().Be(original["id"]!.GetValue<uint>());
            (await set.WaitAsync(token)).Should().Be(new SetResult(SetStatus.Completed, 5));
            var writes = context.RequestsFor("setting.write", "motion.");
            writes.Should().HaveCount(6);
            writes.Count(request => request["id"]!.GetValue<uint>() == original["id"]!.GetValue<uint>()).Should().Be(2);
            device.IsArmed.Should().BeTrue();
        }
        finally
        {
            context.TimerCreated = null;
            context.Write = null;
            foreach (var request in context.RequestsFor("setting.write", "motion."))
            {
                context.Complete(request);
            }

            await set;
        }
    }

    [Theory]
    [InlineData(null, 2000)]
    [InlineData(1, 250)]
    [InlineData(2, 250)]
    public async Task UnansweredRequestFailsConnectionAfterBoundedResends(int? maximumRequestAttempts, int responseTimeoutMilliseconds)
    {
        var responseTimeout = TimeSpan.FromMilliseconds(responseTimeoutMilliseconds);
        if (maximumRequestAttempts is { } attempts)
        {
            context.DeviceOptions.MaximumRequestAttempts = attempts;
            context.DeviceOptions.ResponseTimeout = responseTimeout;
        }

        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var failed = WhenConnectionFails(device);
        var expectedAttempts = maximumRequestAttempts ?? 3;
        var observations = ObserveRequestAttempts(expectedAttempts, responseTimeout);

        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        try
        {
            await AssertBoundedResendsFailAsync(device, set, failed, observations, expectedAttempts, responseTimeout, token);
        }
        finally
        {
            context.TimerCreated = null;
            context.Write = null;
            foreach (var request in context.RequestsFor("setting.write", "motion."))
            {
                context.Complete(request);
            }

            try
            {
                await set;
            }
            catch (DeviceWriteException)
            {
            }
        }
    }

    [Fact]
    public async Task ResponsesForUnknownRequestsAreIgnored()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);

        context.Respond(new JsonObject { ["id"] = 999u }, "completed");
        context.Respond(new JsonObject { ["id"] = 0u }, "failed", ok: false, code: "invalid_request");

        device.IsArmed.Should().BeTrue();
        device.ConnectionFailure.Should().BeNull();
    }

    private (TaskCompletionSource<JsonObject>[] Transmissions, TaskCompletionSource[] Timers) ObserveRequestAttempts(
        int expectedAttempts,
        TimeSpan responseTimeout)
    {
        var transmissions = Enumerable.Range(0, expectedAttempts)
            .Select(_ => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var timers = Enumerable.Range(0, expectedAttempts)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var transmissionCount = 0;
        var timerCount = 0;
        context.TimerCreated = dueTime =>
        {
            if (dueTime == responseTimeout)
            {
                var index = Interlocked.Increment(ref timerCount) - 1;
                if (index < timers.Length)
                {
                    timers[index].TrySetResult();
                }
            }
        };
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request))
            {
                return null;
            }

            var index = Interlocked.Increment(ref transmissionCount) - 1;
            if (index < transmissions.Length)
            {
                transmissions[index].TrySetResult(request);
            }

            return Observable.Return(BleTestContext.WriteResult([]));
        };

        return (transmissions, timers);
    }

    private async Task AssertBoundedResendsFailAsync(
        IOssmDevice device,
        Task<SetResult> set,
        Task failed,
        (TaskCompletionSource<JsonObject>[] Transmissions, TaskCompletionSource[] Timers) observations,
        int expectedAttempts,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken)
    {
        var original = await observations.Transmissions[0].Task.WaitAsync(cancellationToken);
        for (var attempt = 0; attempt < expectedAttempts; attempt++)
        {
            var request = await observations.Transmissions[attempt].Task.WaitAsync(cancellationToken);
            await observations.Timers[attempt].Task.WaitAsync(cancellationToken);
            request["id"]!.GetValue<uint>().Should().Be(original["id"]!.GetValue<uint>());
            context.RequestsFor("setting.write", "motion.").Should().HaveCount(attempt + 1);
            device.ConnectionFailure.Should().BeNull();
            set.IsCompleted.Should().BeFalse();

            context.Clock.Advance(responseTimeout);
        }

        await failed.WaitAsync(cancellationToken);
        device.ConnectionFailure.Should().BeOfType<IOException>().Which.Message.Should().Contain("did not answer");
        device.IsArmed.Should().BeFalse();
        var writes = context.RequestsFor("setting.write", "motion.");
        writes.Should().HaveCount(expectedAttempts);
        writes.Select(request => request["id"]!.GetValue<uint>()).Distinct().Should().ContainSingle();
        var result = () => set;
        await result.Should().ThrowAsync<DeviceWriteException>();
    }
}

public sealed class OssmDeviceLifecycleTests : OssmDeviceTestBase
{
    [Fact]
    public async Task RenewsLeaseAtHalfItsReportedLifetime()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        await device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);

        await device.DisconnectAsync(token);

        context.Requests.Select(BleTestContext.Operation).Should().EndWith(["setting.write", "setting.write", "stream.stop", "control.release"]);
        var stop = context.RequestsFor("setting.write", "motion.")[5];
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
        context.RequestsFor("setting.write", "motion.").Should().HaveCount(6);
        context.RequestsFor("control.acquire").Should().HaveCount(2);
        context.RequestsFor("stream.start")[1]["lease"]!.GetValue<uint>().Should().Be(43);
    }

    [Fact]
    public async Task FailedStopDuringDisconnectStillReleasesLeaseAndAllowsReconnect()
    {
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request))
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
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request))
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
        context.RequestsFor("setting.write", "motion.").Should().ContainSingle();
    }

    [Fact]
    public async Task DisposeAwaitsIssuedTransferAndDisconnectAcknowledgement()
    {
        context.CompleteDisconnect = false;
        var token = TestContext.Current.CancellationToken;
        var device = await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request)
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
            context.RequestsFor("setting.write", "motion.").Should().ContainSingle();
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
            .Should().Equal("control.acquire", "stream.start", "setting.write", "target.set", "setting.write", "setting.write", "stream.stop", "control.release");
        context.RequestsFor("setting.write", "motion.").Should().HaveCount(2);
        device.ConnectionFailure.Should().BeNull();
        context.RequestsFor("stream.stop").Should().ContainSingle();
        context.RequestsFor("control.release").Should().ContainSingle();
        context.DisconnectCalls.Should().Be(1);
    }

    [Fact]
    public async Task DisposeSendsLeasedStopBeforeReleasingControl()
    {
        var device = await context.ConnectAsync(Component, TestContext.Current.CancellationToken);

        await device.DisposeAsync();

        context.Requests.Select(BleTestContext.Operation)
            .Should().Equal("control.acquire", "stream.start", "setting.write", "stream.stop", "control.release");
        var stop = context.RequestsFor("setting.write", "motion.").Should().ContainSingle().Which;
        stop["path"]!.GetValue<string>().Should().Be("motion.speed");
        stop["args"]!["value"]!.GetValue<double>().Should().Be(0);
        stop["lease"]!.GetValue<uint>().Should().Be(42);
    }

    [Fact]
    public async Task DisposeAfterConnectionLossSendsNoStop()
    {
        var device = await context.ConnectAsync(Component, TestContext.Current.CancellationToken);
        context.Status.OnNext(ConnectionState.Disconnected);

        await device.DisposeAsync();

        context.RequestsFor("setting.write", "motion.").Should().BeEmpty();
    }

    [Fact]
    public async Task UnavailableSelectionDoesNotConnectOrDisconnectAnotherDevice()
    {
        context.Manager.Setup(manager => manager.GetKnownPeripheral("device-1")).Returns((IPeripheral?)null);
        var device = Component;

        var connect = () => device.ConnectAsync(TestContext.Current.CancellationToken);
        await connect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unavailable*");
        await device.DisposeAsync();

        context.ConnectionConfiguration.Should().BeNull();
        context.DisconnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task CancellingSubscriptionSetupDisconnectsBeforeReturning()
    {
        context.CompleteSubscription = false;
        var device = Component;
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
        context.Peripheral.Setup(peripheral => peripheral.Connect(Moq.It.IsAny<ConnectionConfig>())).Callback(() => { });
        var first = Component;
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
        context.CompleteSubscription = false;
        var device = Component;
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

}
