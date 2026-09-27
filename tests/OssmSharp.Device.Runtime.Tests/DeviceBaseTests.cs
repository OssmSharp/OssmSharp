using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OneOf;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Device.Runtime.Tests;

/// <summary>
/// Checks device connection, state publication, dispatch, and disposal behavior through a controlled transport.
/// </summary>
public sealed class DeviceBaseTests : MockerTestBase<TestDevice>, IAsyncDisposable
{
    private bool _expectDisposalTimeout;

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

    [Fact]
    public async Task MatchingPendingSpeedDoesNotExcuseUnexpectedDepthChange()
    {
        var device = Component;
        var initial = Settings(10);
        device.ReportState(State(initial, DateTime.UnixEpoch));
        var requested = Settings(30) with { Depth = 50 };
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var reported = State(requested with { Depth = 51 }, DateTime.UnixEpoch.AddSeconds(1));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        device.ReportState(reported);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Same(initial, error.PreviousSettings);
                Assert.Same(reported.MotionSettings, error.CurrentSettings);
                Assert.Same(requested, error.PendingRequestedSettings);
            });
    }

    [Fact]
    public async Task PartialSettingsReportsDoNotLockOrPrematurelyClearPendingSpeed()
    {
        var device = Component;
        var initial = Settings(0);
        device.ReportState(State(initial, DateTime.UnixEpoch));
        var requested = initial with { Speed = 30, Depth = 50 };
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        device.ReportState(State(initial, DateTime.UnixEpoch.AddMilliseconds(100)));
        device.ReportState(State(initial with { Depth = 50 }, DateTime.UnixEpoch.AddMilliseconds(200)));
        device.ReportState(State(requested, DateTime.UnixEpoch.AddMilliseconds(300)));

        Assert.Equal(3, changes.Count);
        Assert.All(changes, change => Assert.True(change.IsT0));
    }

    [Fact]
    public async Task OwnedFeedbackWithWrongTuplePublishesSettingsError()
    {
        var device = Component;
        var initial = Settings(10);
        device.ReportState(State(initial, DateTime.UnixEpoch));
        var requested = Settings(30) with { Depth = 50 };
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var reported = State(requested with { Depth = 51 }, DateTime.UnixEpoch.AddSeconds(1));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        device.ReportState(reported, true);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Same(initial, error.PreviousSettings);
                Assert.Same(reported.MotionSettings, error.CurrentSettings);
                Assert.Same(requested, error.PendingRequestedSettings);
            });
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    public async Task PendingExpectationExpiresAtLifetimeBoundary(
        int elapsedMilliseconds,
        bool expectUnexpectedSpeed)
    {
        var device = Component;
        var timeProvider = Mocks.GetRequiredObject<FakeTimeProvider>();
        device.ReportState(State(Settings(10), DateTime.UnixEpoch));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        await device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));
        var reported = State(Settings(30), timeProvider.GetUtcNow().UtcDateTime);

        device.ReportState(reported);

        Assert.Same(reported, changes[0].AsT0);
        if (expectUnexpectedSpeed)
        {
            var error = Assert.IsType<UnexpectedMotionChangeException>(changes[1].AsT1);
            Assert.Equal(10, error.PreviousSettings.Speed);
            Assert.Same(reported.MotionSettings, error.CurrentSettings);
            Assert.Null(error.PendingRequestedSettings);
            Assert.Equal(2, changes.Count);
        }
        else
        {
            Assert.Single(changes);
        }
    }

    [Fact]
    public async Task FailedDispatchClearsPendingExpectationWithoutPublishingTransportError()
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var transportError = new IOException("Dispatch failed.");
        device.FailDispatchWith(transportError);

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken));

        Assert.Same(transportError, thrown);
        Assert.Empty(changes);
        Assert.Equal(1, device.DispatchCount);

        var reported = State(Settings(30), DateTime.UnixEpoch.AddSeconds(1));
        device.ReportState(reported);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Equal(10, error.PreviousSettings.Speed);
                Assert.Same(reported.MotionSettings, error.CurrentSettings);
                Assert.Null(error.PendingRequestedSettings);
            });
    }

    [Fact]
    public async Task IssuedWriteFailureKeepsExpectationUntilFeedbackOrDeadline()
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch));
        device.IssueBeforeFailure = true;
        device.FailDispatchWith(new IOException("Failed after issuing the first write."));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        await Assert.ThrowsAsync<IOException>(() =>
            device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken));
        var reported = State(Settings(30), DateTime.UnixEpoch.AddMilliseconds(100));
        device.ReportState(reported);

        Assert.Same(reported, Assert.Single(changes).AsT0);
    }

    [Fact]
    public async Task ExplicitForeignStateDoesNotSettlePendingExpectation()
    {
        var device = Component;
        device.ReportState(State(Settings(10), DateTime.UnixEpoch));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        await device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);
        var reported = State(Settings(30), DateTime.UnixEpoch.AddSeconds(1));

        device.ReportState(reported, false);
        device.ReportState(reported);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Equal(30, error.PendingRequestedSettings?.Speed);
                Assert.Same(reported.MotionSettings, error.CurrentSettings);
            },
            change => Assert.Same(reported, change.AsT0));
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
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Equal(10, error.PreviousSettings.Speed);
                Assert.Same(reported.MotionSettings, error.CurrentSettings);
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
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Equal(10, error.PreviousSettings.Speed);
                Assert.Same(reported.MotionSettings, error.CurrentSettings);
                Assert.Null(error.PendingRequestedSettings);
                Assert.Equal(reported.Timestamp, error.StateTimestamp);
            });
    }

    [Fact]
    public async Task UnexpectedSpeedAndDepthPublishOneFullMotionErrorWithOriginalPendingRequest()
    {
        var device = Component;
        var previous = Settings(10) with { Depth = 45, Position = 1.5 };
        device.ReportState(State(previous, DateTime.UnixEpoch));
        var requested = Settings(30) with { Depth = 50, Position = 2.5 };
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var current = Settings(20) with { Depth = 55, Position = 3.5 };
        var reported = State(current, DateTime.UnixEpoch.AddSeconds(1));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        device.ReportState(reported);

        Assert.Collection(
            changes,
            change => Assert.Same(reported, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Same(previous, error.PreviousSettings);
                Assert.Same(current, error.CurrentSettings);
                Assert.Same(requested, error.PendingRequestedSettings);
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
        Mocks.VerifyLogged(LogLevel.Warning, "A device-change subscriber failed", TimesSpec.Exactly(2));
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
    public async Task CancellationWhileWaitingForDispatchDoesNotReachTransport()
    {
        var device = Component;
        device.BlockDispatch = true;
        var firstWrite = device.SetMotionAsync(Settings(20), TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var canceledWrite = device.SetMotionAsync(Settings(30), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWrite);
        Assert.False(firstWrite.IsCompleted);
        Assert.Equal(1, device.DispatchCount);

        device.CompleteDispatch();
        await firstWrite;

        Assert.Equal(1, device.DispatchCount);
    }

    [Fact]
    public async Task DisposalIsIdempotentRejectsWritesAndSuppressesLatePublication()
    {
        var device = Component;
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;

        await device.DisposeAsync();
        await device.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            device.SetMotionAsync(Settings(0), TestContext.Current.CancellationToken));
        device.ReportState(State(Settings(0), DateTime.UnixEpoch));
        device.Fail(new IOException("Late failure."));

        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.True(device.CoreDisposed.IsCompletedSuccessfully);
        Assert.Empty(connections);
        Assert.Empty(changes);
        Assert.Equal(0, device.DispatchCount);
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

    [Fact]
    public async Task DisposalTimesOutWhileIssuedDispatchRemainsUnsettled()
    {
        var device = Component;
        var clock = Mocks.GetRequiredObject<FakeTimeProvider>();
        device.BlockDispatch = true;
        await device.StartAsync(TestContext.Current.CancellationToken);
        var write = device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        _expectDisposalTimeout = true;
        var concurrent = device.DisposeAsync().AsTask();
        TimeoutException? firstFailure = null;
        try
        {
            Assert.False(concurrent.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(6));
            firstFailure = await Assert.ThrowsAsync<TimeoutException>(() =>
                disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(disposal.IsCompleted);
            var concurrentFailure = await Assert.ThrowsAsync<TimeoutException>(() => concurrent);
            Assert.Same(firstFailure, concurrentFailure);
            Assert.False(device.CoreDisposed.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                device.SetMotionAsync(Settings(0), TestContext.Current.CancellationToken));
            Assert.Equal(1, device.DispatchCount);
        }
        finally
        {
            device.CompleteDispatch();
            await write.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        var repeatedFailure = await Assert.ThrowsAsync<TimeoutException>(() => device.DisposeAsync().AsTask());
        Assert.Same(firstFailure, repeatedFailure);
        Assert.False(device.CoreDisposed.IsCompleted);
    }

    [Fact]
    public async Task DisposalBoundsCoreCleanupWithoutRepeatingIt()
    {
        var device = Component;
        var clock = Mocks.GetRequiredObject<FakeTimeProvider>();
        device.BlockCoreDisposal = true;
        var disposal = device.DisposeAsync().AsTask();
        _expectDisposalTimeout = true;

        try
        {
            await device.CoreDisposalStarted.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(6));
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(disposal.IsFaulted);
            Assert.False(device.CoreDisposed.IsCompleted);
            Assert.Equal(1, device.CoreDisposalCount);
            var repeated = await Assert.ThrowsAsync<TimeoutException>(() => device.DisposeAsync().AsTask());
            Assert.Same(failure, repeated);
        }
        finally
        {
            device.CompleteCoreDisposal();
            await device.CoreDisposed.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, device.CoreDisposalCount);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForSettlementAndCleansUpOnce()
    {
        var device = Component;
        device.BlockDispatch = true;
        await device.StartAsync(TestContext.Current.CancellationToken);
        var write = device.SetMotionAsync(Settings(30), TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);

        var first = device.DisposeAsync().AsTask();
        var second = device.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(device.CoreDisposed.IsCompleted);
        }
        finally
        {
            device.CompleteDispatch();
            await write.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await first.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await second.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        Assert.True(device.CoreDisposed.IsCompletedSuccessfully);
        Assert.Equal(1, device.CoreDisposalCount);
    }

    [Fact]
    public async Task DisposalTimesOutWithoutReleasingAnUnsettledConnection()
    {
        var device = Component;
        var clock = Mocks.GetRequiredObject<FakeTimeProvider>();
        device.IgnoreStopCancellation = true;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };

        try
        {
            await device.StartAsync(TestContext.Current.CancellationToken);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var disposal = device.DisposeAsync().AsTask();
            _expectDisposalTimeout = true;
            clock.Advance(TimeSpan.FromSeconds(6));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

            Assert.True(disposal.IsCompleted);
            Assert.False(device.CoreDisposed.IsCompleted);
            Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);
        }
        finally
        {
            device.EndTransport();
            if (device.ExecuteTask is { } transport)
            {
                await transport.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            Component.CompleteDispatch();
            Component.CompleteCoreDisposal();
            if (_expectDisposalTimeout)
            {
                Component.EndTransport();
                if (Component.ExecuteTask is { } transport)
                {
                    try
                    {
                        await transport.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (OperationCanceledException) when (transport.IsCanceled)
                    {
                        // Host Stop may cancel ExecuteTask after disposal's timeout;
                        // cancellation is settled transport lifetime, not a cleanup fault.
                    }
                }
            }
            else
            {
                await Component.DisposeAsync();
            }
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

}
