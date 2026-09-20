using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OneOf;

namespace OSSMDotNet.Tests;

public sealed class OssmDeviceBaseTests : MockerTestBase<TestDevice>, IAsyncDisposable
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.AddType(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        mocks.AddMethodResult<ILogger<TestDevice>, bool>(
            logger => logger.IsEnabled(FastArg.Any<LogLevel>()),
            true);
    };

    [Fact]
    public async Task StartPublishesConnectionFactsWithoutState()
    {
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var connections = Channel.CreateUnbounded<(DeviceConnectionState Event, DeviceConnectionState Property, DeviceInformation? Device)>();
        device.ConnectionStateChanged += state => connections.Writer.TryWrite((
            state, device.ConnectionState, state == DeviceConnectionState.Connected ? device.Device : null));

        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => device.Device);
        Assert.False(connections.Reader.TryRead(out _));
        await device.StartAsync(TestContext.Current.CancellationToken);
        var connecting = await connections.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var connected = await connections.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeviceConnectionState.Connecting, connecting.Event);
        Assert.Equal(connecting.Event, connecting.Property);
        Assert.Equal(DeviceConnectionState.Connected, connected.Event);
        Assert.Equal(connected.Event, connected.Property);
        Assert.Same(TestDevice.SimulatorInformation, connected.Device);
        Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);
        Assert.Empty(changes);
        Assert.False(connections.Reader.TryRead(out _));
        Mocks.VerifyLogged(LogLevel.Information, "Device connection changed", TimesSpec.Exactly(2));
    }

    [Fact]
    public void ConnectionTransitionsRetainIdentityAndDoNotPublishState()
    {
        var device = Component;
        device.Connect();
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += _ => throw new InvalidOperationException("subscriber failure");
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;
        var error = new IOException("transport failed");

        device.Disconnect();
        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Same(TestDevice.SimulatorInformation, device.Device);
        Assert.Empty(changes);
        device.Connect();
        device.Connect();
        device.Fail(error);

        Assert.Equal(
            [DeviceConnectionState.Disconnected, DeviceConnectionState.Connected],
            connections);
        Assert.Same(TestDevice.SimulatorInformation, device.Device);
        Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);
        Assert.Same(error, Assert.Single(changes).AsT1);
    }

    [Fact]
    public async Task DisconnectClearsStateMatchingContext()
    {
        var device = Component;
        device.Connect();
        await device.SetMotionAsync(Settings(40), TestContext.Current.CancellationToken);
        device.ReportState(State(Settings(20), DateTime.UnixEpoch));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        device.Disconnect();
        device.Connect();
        var reported = State(Settings(0), DateTime.UnixEpoch.AddSeconds(1));
        device.ReportState(reported);

        Assert.Single(changes);
        Assert.Same(reported, changes[0].AsT0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportedErrorDoesNotChangeConnectionState(bool connected)
    {
        var device = Component;
        if (connected)
        {
            device.Connect();
        }

        var expected = device.ConnectionState;
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;
        var error = new InvalidDataException("Invalid state.");

        device.Fail(error);

        Assert.Equal(expected, device.ConnectionState);
        Assert.Empty(connections);
        Assert.Same(error, Assert.Single(changes).AsT1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailurePublishesDisconnectedWithoutStateErrorAndLogsWarning(bool connectsFirst)
    {
        var device = Component;
        device.ConnectOnStart = connectsFirst;
        var changes = new List<OneOf<DeviceState, Exception>>();
        var connections = Channel.CreateUnbounded<DeviceConnectionState>();
        device.ConnectionStateChanged += state => connections.Writer.TryWrite(state);
        device.DeviceStateChanged += changes.Add;
        await device.StartAsync(TestContext.Current.CancellationToken);

        device.EndTransport(new IOException("Transport closed."));

        Assert.Equal(DeviceConnectionState.Connecting, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        if (connectsFirst)
        {
            Assert.Equal(DeviceConnectionState.Connected, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Same(TestDevice.SimulatorInformation, device.Device);
        }

        Assert.Equal(DeviceConnectionState.Disconnected, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        await device.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Empty(changes);
        Assert.False(connections.Reader.TryRead(out _));
        Mocks.VerifyLogged(LogLevel.Warning, "Device connection failed", TimesSpec.Once);
    }

    [Fact]
    public async Task NormalTransportExitPublishesDisconnectedWithoutStateError()
    {
        var device = Component;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };
        device.DeviceStateChanged += changes.Add;
        await device.StartAsync(TestContext.Current.CancellationToken);

        device.EndTransport();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await device.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task FirstSettledWriteDoesNotPublishState(int speed)
    {
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var requested = Settings(speed);

        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);

        Assert.Equal(1, device.DispatchCount);
        Assert.Empty(changes);

        var reported = State(requested, DateTime.UnixEpoch, 0);
        device.ReportState(reported);

        Assert.Same(reported, Assert.Single(changes).AsT0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void StatePublicationDistinguishesUnknownVelocityFromReportedZero(double? velocity)
    {
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var settings = Settings(0);
        var reported = State(settings, DateTime.UnixEpoch, velocity);

        device.ReportState(reported);

        var published = Assert.Single(changes).AsT0;
        Assert.Same(reported, published);
        Assert.Same(settings, published.MotionSettings);
        Assert.Equal(DateTime.UnixEpoch, published.Timestamp);
        Assert.Equal(velocity, published.VelocityMillimetresPerSecond);
    }

    [Fact]
    public async Task SettledWriteAfterReportedStateDoesNotPublishState()
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch, 12.5));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        await device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);

        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified, true, true)]
    [InlineData(DateTimeKind.Local, true, true)]
    [InlineData(DateTimeKind.Unspecified, false, true)]
    [InlineData(DateTimeKind.Local, false, true)]
    [InlineData(DateTimeKind.Utc, false, false)]
    [InlineData(DateTimeKind.Unspecified, true, false)]
    public async Task InvalidStateDoesNotClearPendingExpectation(
        DateTimeKind timestampKind,
        bool defaultTimestamp,
        bool hasSettings)
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch, 12.5));
        var requested = Settings(30);
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var invalid = new DeviceState
        {
            Timestamp = DateTime.SpecifyKind(defaultTimestamp ? default : DateTime.UnixEpoch.AddSeconds(1), timestampKind),
            MotionSettings = hasSettings ? requested : null!,
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = -12.5,
        };

        Assert.Throws<InvalidDataException>(() => device.ReportState(invalid));
        Assert.Empty(changes);

        var reported = State(requested, DateTime.UnixEpoch.AddSeconds(1), -12.5);
        device.ReportState(reported);

        Assert.Same(reported, Assert.Single(changes).AsT0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidStateDoesNotAlterSpeedBaseline(double velocity)
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch, 12.5));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        Assert.Throws<InvalidDataException>(() =>
            device.ReportState(State(Settings(20), DateTime.UnixEpoch.AddSeconds(1), velocity)));
        Assert.Empty(changes);

        var reported = State(Settings(30), DateTime.UnixEpoch.AddSeconds(2), -12.5);
        device.ReportState(reported);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedSpeedChangeException>(change.AsT1);
                Assert.Equal(10, error.PreviousStateSpeed);
                Assert.Equal(30, error.StateSpeed);
            });
    }

    [Fact]
    public async Task RegistersPendingWriteBeforeDispatchAndPublishesItsSynchronousStateOnce()
    {
        var device = Component;
        device.ReportStateDuringDispatch = true;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        await device.StartAsync(TestContext.Current.CancellationToken);

        await device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);

        var published = Assert.Single(changes).AsT0;
        Assert.Equal(30, published.MotionSettings.Speed);
        Assert.Equal(1, device.DispatchCount);
    }

    [Fact]
    public async Task SettledWriteUsesPriorRequestToMatchDelayedScaledState()
    {
        await using var device = new StockSpeedTestDevice(Mocks.GetRequiredObject<FakeTimeProvider>());
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        await device.SetMotionAsync(Settings(60), TestContext.Current.CancellationToken);
        device.ReportState(Settings(30));
        await device.SetMotionAsync(Settings(40), TestContext.Current.CancellationToken);
        device.ReportState(Settings(20));

        Assert.Collection(
            changes,
            change => Assert.Equal(30, change.AsT0.MotionSettings.Speed),
            change => Assert.Equal(20, change.AsT0.MotionSettings.Speed));
    }

    [Fact]
    public void UnexpectedSpeedPublishesStateBeforeException()
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var reported = State(Settings(20), DateTime.UnixEpoch.AddSeconds(1));

        device.ReportState(reported);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedSpeedChangeException>(change.AsT1);
                Assert.Equal(10, error.PreviousStateSpeed);
                Assert.Equal(20, error.StateSpeed);
                Assert.Null(error.PendingRequestedSpeed);
                Assert.Equal(reported.Timestamp, error.StateTimestamp);
            });
    }

    [Fact]
    public async Task SubscriberFailureDoesNotPreventLaterSubscribersOrBecomeDeviceError()
    {
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += _ => throw new InvalidOperationException("subscriber failure");
        device.DeviceStateChanged += changes.Add;
        var reported = State(Settings(0), DateTime.UnixEpoch);
        var error = new IOException("transport failed");

        await device.StartAsync(TestContext.Current.CancellationToken);
        await device.SetMotionAsync(Settings(0), TestContext.Current.CancellationToken);
        Assert.Empty(changes);
        device.ReportState(reported);
        await device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);
        device.Fail(error);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change => Assert.Same(error, change.AsT1));
    }

    [Fact]
    public async Task InvalidSettingsAndPredispatchCancellationNeverReachTransportOrFaultEvent()
    {
        var device = Component;
        var reportedErrors = 0;
        device.DeviceStateChanged += change => change.Switch(_ => { }, _ => reportedErrors++);
        await device.StartAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            device.SetMotionAsync(Settings(10) with { Stroke = 0 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            device.SetMotionAsync(Settings(10), cancellation.Token));

        Assert.Equal(0, device.DispatchCount);
        Assert.Equal(0, reportedErrors);
    }

    [Fact]
    public async Task CancellationAfterDispatchWaitsForSettlementWithoutPublishingState()
    {
        var device = Component;
        device.BlockDispatch = true;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        await device.StartAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var requested = Settings(30);

        var write = device.SetMotionAsync(requested, cancellation.Token);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        Assert.False(write.IsCompleted);
        device.CompleteDispatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Empty(changes);

        var reported = State(requested, DateTime.UnixEpoch);
        device.ReportState(reported);

        Assert.Equal(1, device.DispatchCount);
        Assert.Same(reported, Assert.Single(changes).AsT0);
    }

    [Fact]
    public void UtcMinimumTimestampPublishesState()
    {
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var reported = State(
            Settings(0),
            DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));

        device.ReportState(reported);

        Assert.Same(reported, Assert.Single(changes).AsT0);
    }

    [Fact]
    public async Task DisposalWaitsForIssuedDispatchBeforeReleasingTransportResources()
    {
        var device = Component;
        device.BlockDispatch = true;
        await device.StartAsync(TestContext.Current.CancellationToken);
        var write = device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                device.CoreDisposed.WaitAsync(
                    TimeSpan.FromMilliseconds(100),
                    TestContext.Current.CancellationToken));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            device.CompleteDispatch();
            try
            {
                await write;
            }
            finally
            {
                await disposal;
            }
        }

        Assert.True(device.CoreDisposed.IsCompletedSuccessfully);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            Component.CompleteDispatch();
            await Component.DisposeAsync();
        }
        finally
        {
            base.Dispose();
        }
    }

    private static MotionSettings Settings(int speed) => new(
        MotionDeviceState.StrokeEngineIdle,
        speed,
        20,
        50,
        40,
        0,
        MotionPattern.SimpleStroke,
        0);

    private static DeviceState State(
        MotionSettings settings,
        DateTime timestamp,
        double? velocity = null) => new()
        {
            Timestamp = timestamp,
            MotionSettings = settings,
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = velocity,
        };

    private sealed class StockSpeedTestDevice(FakeTimeProvider timeProvider)
        : OssmDeviceBase(
            new OssmDeviceOptions
            {
                ExpectationLifetime = TimeSpan.FromSeconds(1),
            },
            timeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        public void ReportState(MotionSettings settings) => PublishState(
            new DeviceState
            {
                Timestamp = timeProvider.GetUtcNow().UtcDateTime,
                MotionSettings = settings,
                SessionId = Guid.Empty,
            },
            null);

        protected override Task ConnectAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override Task DispatchMotionAsync(
            MotionSettings motionSettings,
            CancellationToken cancellationToken) => Task.CompletedTask;

        protected override bool IsExpectedState(
            int? previousRequestedSpeed,
            int? previousStateSpeed,
            int requestedSpeed,
            int stateSpeed) => OssmDevice.IsExpectedStateImpl(
                previousRequestedSpeed,
                previousStateSpeed,
                requestedSpeed,
                stateSpeed);
    }
}
