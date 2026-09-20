using System.Threading.Channels;
using FastMoq;
using Microsoft.Extensions.Time.Testing;
using OneOf;

namespace OSSMDotNet.Tests;

public sealed class OssmDeviceBaseTests : MockerTestBase<TestDevice>, IAsyncDisposable
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
        mocks.AddType(new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task StartsWithoutObservationThenPublishesConnectedDeviceFacts()
    {
        var device = Component;
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        var connections = Channel.CreateUnbounded<(DeviceConnectionState Event, DeviceConnectionState Property, DeviceInformation? Device)>();
        device.ConnectionStateChanged += state => connections.Writer.TryWrite((
            state, device.ConnectionState, state == DeviceConnectionState.Connected ? device.Device : null));

        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
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
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);
        Assert.False(changes.Reader.TryRead(out _));
        Assert.False(connections.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTelemetryReadFailsWithoutChangingConnectionOrPublishing(bool connected)
    {
        var device = Component;
        if (connected)
        {
            device.Connect();
        }

        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;

        var read = device.GetStateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => read);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected, device.ConnectionState);
        if (connected)
        {
            Assert.Same(TestDevice.SimulatorInformation, device.Device);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => device.Device);
        }

        Assert.Equal(0, device.DispatchCount);
        Assert.Empty(connections);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledReadTakesPrecedenceOverObservationAvailability(bool hasObservation)
    {
        var device = Component;
        device.Connect();
        if (hasObservation)
        {
            device.Observe(Settings(10), DateTime.UnixEpoch, 12.5);
        }

        var initial = hasObservation ? await device.GetStateAsync(TestContext.Current.CancellationToken) : null;
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.GetStateAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (hasObservation)
        {
            Assert.NotNull(initial);
            Assert.Same(initial, await device.GetStateAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);
        Assert.Same(TestDevice.SimulatorInformation, device.Device);
        Assert.Equal(0, device.DispatchCount);
        Assert.Empty(connections);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposedReadTakesPrecedenceOverCancellationAndObservationAvailability(bool hasObservation, bool canceled)
    {
        var device = Component;
        if (hasObservation)
        {
            device.Observe(Settings(10), DateTime.UnixEpoch, 12.5);
        }

        await device.DisposeAsync();
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (canceled)
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.GetStateAsync(cancellation.Token));

        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Equal(0, device.DispatchCount);
        Assert.Empty(connections);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task ObservationAgesToLastKnownAndRemainsAccessibleAfterDisconnection()
    {
        var time = Mocks.GetRequiredObject<FakeTimeProvider>();
        var device = Component;
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        await device.StartAsync(TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);
        var requested = Settings(10);
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var observedAt = time.GetUtcNow().UtcDateTime;
        var sessionId = Guid.Parse("a8d3e997-8ccd-42be-bb46-2c96684024a6");
        device.Observe(new DeviceState
        {
            Timestamp = observedAt,
            MotionSettings = Settings(10),
            SessionId = sessionId,
            VelocityMillimetresPerSecond = -12.5,
        });
        var live = await ReadStateAsync(changes.Reader, state => state.ObservationFreshness == ObservationFreshness.Live);

        time.Advance(TimeSpan.FromSeconds(3));
        var lastKnown = await ReadStateAsync(changes.Reader, state => state.ObservationFreshness == ObservationFreshness.LastKnown);

        Assert.Same(live.MotionSettings, lastKnown.MotionSettings);
        Assert.Same(requested, lastKnown.RequestedMotion);
        Assert.Equal(observedAt, lastKnown.Timestamp);
        Assert.Equal(10, lastKnown.MotionSettings.Speed);
        Assert.Equal(sessionId, lastKnown.SessionId);
        Assert.Equal(-12.5, lastKnown.VelocityMillimetresPerSecond);
        Assert.Equal(live with { ObservationFreshness = ObservationFreshness.LastKnown }, lastKnown);
        Assert.Equal(ObservationFreshness.Live, live.ObservationFreshness);
        Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);

        var connections = new List<DeviceConnectionState>();
        device.ConnectionStateChanged += connections.Add;
        device.Disconnect();

        Assert.Same(lastKnown, await device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Same(TestDevice.SimulatorInformation, device.Device);
        Assert.Equal([DeviceConnectionState.Disconnected], connections);
        Assert.False(changes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ConnectionTransitionsRetainIdentityAndDoNotRepublishMotion()
    {
        var device = Component;
        device.Connect();
        device.Observe(Settings(10), DateTime.UnixEpoch, 12.5);
        var initial = await device.GetStateAsync(TestContext.Current.CancellationToken);
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += _ => throw new InvalidOperationException("subscriber failure");
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;
        var error = new IOException("transport failed");

        device.Disconnect();
        Assert.Same(initial, await device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ObservationFreshness.Live, initial.ObservationFreshness);
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
        Assert.Same(initial, await device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Same(error, Assert.Single(changes).AsT1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportedErrorDoesNotChangeConnectionState(bool connected)
    {
        var device = Component;
        if (connected)
        {
            device.Connect();
        }

        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
        var expected = device.ConnectionState;
        var connections = new List<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += connections.Add;
        device.DeviceStateChanged += changes.Add;
        var error = new InvalidDataException("Invalid telemetry.");

        device.Fail(error);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, device.ConnectionState);
        Assert.Empty(connections);
        Assert.Same(error, Assert.Single(changes).AsT1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureReportsDisconnectedSeparatelyFromError(bool connectsFirst)
    {
        var device = Component;
        device.ConnectOnStart = connectsFirst;
        var reported = new TaskCompletionSource<(Exception Error, DeviceConnectionState State)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = Channel.CreateUnbounded<DeviceConnectionState>();
        device.ConnectionStateChanged += state => connections.Writer.TryWrite(state);
        device.DeviceStateChanged += change => change.Switch(
            _ => { }, error => reported.TrySetResult((error, device.ConnectionState)));
        await device.StartAsync(TestContext.Current.CancellationToken);
        var error = new IOException("Transport closed.");

        device.EndTransport(error);
        var result = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Same(error, result.Error);
        Assert.Equal(DeviceConnectionState.Disconnected, result.State);
        Assert.Equal(DeviceConnectionState.Connecting, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        if (connectsFirst)
        {
            Assert.Equal(DeviceConnectionState.Connected, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Same(TestDevice.SimulatorInformation, device.Device);
        }

        Assert.Equal(DeviceConnectionState.Disconnected, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(connections.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NormalTransportExitReportsDisconnectedWithoutError()
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

        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task FirstSettledWriteBeforeTelemetryDoesNotInventAnObservation(int speed)
    {
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
        var requested = Settings(speed);

        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, device.DispatchCount);
        Assert.Empty(changes);

        var observation = new DeviceState
        {
            Timestamp = DateTime.UnixEpoch,
            MotionSettings = Settings(speed),
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = 0,
            RequestedMotion = Settings(99),
        };
        device.Observe(observation);
        var observed = await device.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Same(requested, observed.RequestedMotion);
        Assert.Same(observation.MotionSettings, observed.MotionSettings);
        Assert.Equal(observation with
        {
            RequestedMotion = requested,
            ObservationFreshness = ObservationFreshness.Live,
        }, observed);
        Assert.Equal(ObservationFreshness.Unavailable, observation.ObservationFreshness);
        Assert.Equal(99, observation.RequestedMotion.Speed);
        Assert.Same(observed, Assert.Single(changes).AsT0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public async Task ObservationDistinguishesUnknownVelocityFromReportedZero(double? velocity)
    {
        var device = Component;
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
        var settings = Settings(0);

        device.Observe(settings, DateTime.UnixEpoch, velocity);
        var observed = await device.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Same(settings, observed.MotionSettings);
        Assert.Equal(DateTime.UnixEpoch, observed.Timestamp);
        Assert.Equal(velocity, observed.VelocityMillimetresPerSecond);
        Assert.Equal(ObservationFreshness.Live, observed.ObservationFreshness);
        Assert.Null(observed.RequestedMotion);
    }

    [Fact]
    public async Task SettledWriteDoesNotChangeRetainedObservationTimeOrVelocity()
    {
        var time = Mocks.GetRequiredObject<FakeTimeProvider>();
        var device = Component;
        var sessionId = Guid.Parse("a8d3e997-8ccd-42be-bb46-2c96684024a6");
        device.Observe(new DeviceState
        {
            Timestamp = DateTime.UnixEpoch,
            MotionSettings = Settings(10),
            SessionId = sessionId,
            VelocityMillimetresPerSecond = 12.5,
        });
        var initial = await device.GetStateAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        var requested = Settings(30);
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var settled = await device.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Same(requested, settled.RequestedMotion);
        Assert.Same(initial.MotionSettings, settled.MotionSettings);
        Assert.Equal(DateTime.UnixEpoch, settled.Timestamp);
        Assert.Equal(sessionId, settled.SessionId);
        Assert.Equal(12.5, settled.VelocityMillimetresPerSecond);
        Assert.Equal(initial with { RequestedMotion = requested }, settled);
        Assert.Same(settled, Assert.Single(changes).AsT0);
        Assert.Null(initial.RequestedMotion);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TelemetryPreservesLastSettledRequestAndPreviouslyReturnedState(
        bool settlesWrite,
        bool includesRequestedMotion)
    {
        var device = Component;
        var original = new DeviceState
        {
            Timestamp = DateTime.UnixEpoch,
            MotionSettings = Settings(10),
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = 12.5,
        };
        device.Observe(original);
        var requested = settlesWrite ? Settings(30) : null;
        if (requested is not null)
        {
            await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        }

        var retained = await device.GetStateAsync(TestContext.Current.CancellationToken);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var incoming = new DeviceState
        {
            Timestamp = DateTime.UnixEpoch.AddSeconds(1),
            MotionSettings = Settings(requested?.Speed ?? 10),
            SessionId = Guid.Parse("a8d3e997-8ccd-42be-bb46-2c96684024a6"),
            VelocityMillimetresPerSecond = -12.5,
            RequestedMotion = includesRequestedMotion ? Settings(99) : null,
            ObservationFreshness = ObservationFreshness.LastKnown,
        };

        device.Observe(incoming);
        var current = await device.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Same(requested, current.RequestedMotion);
        Assert.Same(incoming.MotionSettings, current.MotionSettings);
        Assert.Equal(incoming with
        {
            RequestedMotion = requested,
            ObservationFreshness = ObservationFreshness.Live,
        }, current);
        Assert.Equal(current, Assert.Single(changes).AsT0);
        Assert.Equal(original with
        {
            RequestedMotion = requested,
            ObservationFreshness = ObservationFreshness.Live,
        }, retained);
        Assert.Same(original.MotionSettings, retained.MotionSettings);
        Assert.Equal(ObservationFreshness.Unavailable, original.ObservationFreshness);
        Assert.Equal(includesRequestedMotion ? 99 : (int?)null, incoming.RequestedMotion?.Speed);
        Assert.Equal(ObservationFreshness.LastKnown, incoming.ObservationFreshness);
    }

    [Fact]
    public async Task ExplicitUtcMinimumTimestampIsAnObservationNotAnAbsenceSentinel()
    {
        var timestamp = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var observation = new DeviceState
        {
            Timestamp = timestamp,
            MotionSettings = Settings(0),
            SessionId = Guid.Empty,
        };

        Component.Observe(observation);
        var result = await Component.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(observation with { ObservationFreshness = ObservationFreshness.Live }, result);
        Assert.Equal(DateTimeKind.Utc, result.Timestamp.Kind);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified, true, true)]
    [InlineData(DateTimeKind.Local, true, true)]
    [InlineData(DateTimeKind.Unspecified, false, true)]
    [InlineData(DateTimeKind.Local, false, true)]
    [InlineData(DateTimeKind.Utc, false, false)]
    [InlineData(DateTimeKind.Unspecified, true, false)]
    public async Task InvalidObservationIsRejectedBeforeStateOrPendingWriteChanges(
        DateTimeKind timestampKind,
        bool defaultTimestamp,
        bool hasSettings)
    {
        var device = Component;
        device.Observe(Settings(10), DateTime.UnixEpoch, 12.5);
        var requested = Settings(30);
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var initial = await device.GetStateAsync(TestContext.Current.CancellationToken);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;
        var invalid = new DeviceState
        {
            Timestamp = DateTime.SpecifyKind(defaultTimestamp ? default : DateTime.UnixEpoch.AddSeconds(1), timestampKind),
            MotionSettings = hasSettings ? requested : null!,
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = -12.5,
        };

        Assert.Throws<InvalidDataException>(() => device.Observe(invalid));

        Assert.Same(initial, await device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Empty(changes);
        device.Observe(requested, DateTime.UnixEpoch.AddSeconds(1), -12.5);
        Assert.Same(requested, Assert.Single(changes).AsT0.MotionSettings);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task InvalidObservationVelocityDoesNotReplaceRetainedSample(double velocity)
    {
        var device = Component;
        device.Observe(Settings(10), DateTime.UnixEpoch, 12.5);
        var requested = Settings(30);
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        var initial = await device.GetStateAsync(TestContext.Current.CancellationToken);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        Assert.Throws<InvalidDataException>(() => device.Observe(requested, DateTime.UnixEpoch, velocity));

        Assert.Same(initial, await device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Empty(changes);
        device.Observe(requested, DateTime.UnixEpoch.AddSeconds(1), -12.5);
        Assert.Same(requested, Assert.Single(changes).AsT0.MotionSettings);
    }

    [Fact]
    public async Task RegistersPendingWriteBeforeDispatchAndDoesNotReportItsSynchronousObservation()
    {
        var device = Component;
        device.ObserveDuringDispatch = true;
        var errors = Channel.CreateUnbounded<Exception>();
        device.DeviceStateChanged += change => change.Switch(_ => { }, error => errors.Writer.TryWrite(error));
        await device.StartAsync(TestContext.Current.CancellationToken);

        await device.SetMotionAsync(Settings(speed: 30), TestContext.Current.CancellationToken);

        Assert.False(errors.Reader.TryRead(out _));
        Assert.Equal(30, (await device.GetStateAsync(TestContext.Current.CancellationToken)).RequestedMotion?.Speed);
    }

    [Fact]
    public async Task UnexpectedSpeedCommitsStateBeforePublishingTheException()
    {
        var time = Mocks.GetRequiredObject<FakeTimeProvider>();
        var device = Component;
        var report = new TaskCompletionSource<(UnexpectedSpeedChangeException Error, DeviceState State)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.DeviceStateChanged += change => change.Switch(
            _ => { },
            error =>
            {
                var unexpected = Assert.IsType<UnexpectedSpeedChangeException>(error);
                var state = device.GetStateAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                report.TrySetResult((unexpected, state));
            });
        await device.StartAsync(TestContext.Current.CancellationToken);
        device.Observe(Settings(speed: 10), time.GetUtcNow().UtcDateTime);

        device.Observe(Settings(speed: 20), time.GetUtcNow().UtcDateTime);
        var reported = await report.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20, reported.State.MotionSettings.Speed);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotPreventLaterSubscribersOrBecomeADeviceError()
    {
        var time = Mocks.GetRequiredObject<FakeTimeProvider>();
        var device = Component;
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += _ => throw new InvalidOperationException("subscriber failure");
        device.DeviceStateChanged += changes.Add;
        var requested = Settings(0);
        var updated = Settings(30);
        var observation = new DeviceState
        {
            Timestamp = time.GetUtcNow().UtcDateTime,
            MotionSettings = Settings(0),
            SessionId = Guid.Empty,
        };
        var error = new IOException("transport failed");

        await device.StartAsync(TestContext.Current.CancellationToken);
        await device.SetMotionAsync(requested, TestContext.Current.CancellationToken);
        Assert.Empty(changes);
        device.Observe(observation);
        await device.SetMotionAsync(updated, TestContext.Current.CancellationToken);
        device.Fail(error);

        Assert.Collection(changes,
            change => Assert.Equal(observation with
            {
                RequestedMotion = requested,
                ObservationFreshness = ObservationFreshness.Live,
            }, change.AsT0),
            change => Assert.Equal(observation with
            {
                RequestedMotion = updated,
                ObservationFreshness = ObservationFreshness.Live,
            }, change.AsT0),
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
            device.SetMotionAsync(Settings(speed: 10) with { Stroke = 0 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            device.SetMotionAsync(Settings(speed: 10), cancellation.Token));

        Assert.Equal(0, device.DispatchCount);
        Assert.Equal(0, reportedErrors);
    }

    [Fact]
    public async Task CancellationAfterDispatchWaitsForSettlementBeforeReportingCancellation()
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
        await Assert.ThrowsAsync<NoDeviceDataException>(() => device.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Empty(changes);

        device.Observe(requested, DateTime.UnixEpoch);
        var observed = await device.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, device.DispatchCount);
        Assert.Same(requested, observed.RequestedMotion);
        Assert.Same(observed, Assert.Single(changes).AsT0);
    }

    [Fact]
    public async Task DisposalWaitsForIssuedDispatchBeforeReleasingTransportResources()
    {
        var device = Component;
        device.BlockDispatch = true;
        await device.StartAsync(TestContext.Current.CancellationToken);
        var write = device.SetMotionAsync(Settings(speed: 30), TestContext.Current.CancellationToken);
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
            // Release a gated dispatch if an assertion failed before the test could settle it.
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

    private static async Task<DeviceState> ReadStateAsync(
        ChannelReader<OneOf<DeviceState, Exception>> reader,
        Func<DeviceState, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (await reader.ReadAsync(timeout.Token) is { } change)
        {
            var state = Assert.IsType<DeviceState>(change.Value);
            if (predicate(state))
            {
                return state;
            }
        }

        throw new InvalidOperationException("State channel completed unexpectedly.");
    }
}
