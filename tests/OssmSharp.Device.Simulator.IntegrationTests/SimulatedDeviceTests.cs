using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using OneOf;
using OssmSharp.Device.Simulator.Models;

namespace OssmSharp.Device.Simulator.IntegrationTests;

/// <summary>
/// Exercises simulator process state, writes, external changes, and failure handling through the device contract.
/// </summary>
public sealed class SimulatedDeviceTests : MockerTestBase<SimulatedDevice>, IAsyncDisposable, IClassFixture<SimulatorBridgeFixture>
{
    private const string ValidEventJson =
        """{"v":1,"event":"state","state":{"sampleSequence":1,"changeRevision":0,"commandId":null,"desired":{"pattern":0,"speed":0,"stroke":10,"depth":20,"sensation":50},"enabled":false,"moving":false,"position":0.0,"velocity":0.0,"connected":true,"profileId":"ossm-rs-sim-v1"}}""";
    private readonly SimulatorBridgeFixture _bridge;

    public SimulatedDeviceTests(SimulatorBridgeFixture bridge)
        : base(mocks =>
    {
        mocks.SetupOptions(new SimulatedDeviceOptions { Endpoint = bridge.Endpoint });
        mocks.AddType<TimeProvider>(TimeProvider.System);
    }) => _bridge = bridge;

    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new SimulatedDeviceOptions { Endpoint = "tcp://localhost:1234" };
        Mocks.SetupOptions(options, replace: true);

        Assert.IsAssignableFrom<DeviceRuntimeOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        var exception = Assert.Throws<TargetInvocationException>(() => Mocks.CreateInstance<SimulatedDevice>());
        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tcp://localhost:1234")]
    [InlineData("tcp://127.0.0.2:1234")]
    [InlineData("tcp://[::1]:1234")]
    [InlineData("tcp://127.0.0.1")]
    [InlineData("tcp://127.0.0.1:0")]
    [InlineData("tcp://127.0.0.1:65536")]
    [InlineData("tcp://127.0.0.1:1234/path")]
    [InlineData("tcp://127.0.0.1:1234?query=1")]
    [InlineData("tcp://user@127.0.0.1:1234")]
    public void InvalidSimulatorEndpointIsRejected(string endpoint)
    {
        var options = new SimulatedDeviceOptions { Endpoint = endpoint };

        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), [], true));
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:1")]
    [InlineData("tcp://127.0.0.1:65535")]
    public void ExplicitLoopbackEndpointAcceptsPortBounds(string endpoint)
    {
        var options = new SimulatedDeviceOptions { Endpoint = endpoint };

        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), [], true));
    }

    [Fact]
    public async Task RealSimulatorReportsMotionAndSettledZeroSpeedWrite()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);

        Assert.False(changes.Reader.TryRead(out _));
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => Component.Device);

        await Component.StartAsync(timeout.Token);
        var initial = await ReadStateAsync(
            changes.Reader,
            _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        var moving = Settings(12);

        await Component.SetMotionAsync(moving, timeout.Token);
        var applied = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Speed == moving.Speed,
            timeout.Token);
        await Component.SetMotionAsync(moving with { Speed = 0 }, timeout.Token);
        var stopped = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Speed == 0,
            timeout.Token);

        Assert.Equal("ossm-rs simulator", Component.Device.Identity);
        Assert.Equal("ossm-rs-sim-v1", Component.Device.ProfileId);
        Assert.Equal(DateTimeKind.Utc, initial.Timestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, applied.Timestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, stopped.Timestamp.Kind);
        Assert.True(initial.VelocityMillimetresPerSecond is { } initialVelocity &&
            double.IsFinite(initialVelocity));
        Assert.True(applied.VelocityMillimetresPerSecond is { } appliedVelocity &&
            double.IsFinite(appliedVelocity));
        Assert.True(stopped.VelocityMillimetresPerSecond is { } stoppedVelocity &&
            double.IsFinite(stoppedVelocity));
        Assert.Equal(moving.Stroke, stopped.MotionSettings.Stroke);
        Assert.Equal(moving.Depth, stopped.MotionSettings.Depth);
        Assert.Equal(moving.Pattern, stopped.MotionSettings.Pattern);

        var information = Component.Device;
        Drain(changes.Reader);
        await Component.StopAsync(timeout.Token);

        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Same(information, Component.Device);
        Assert.False(changes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ZeroSpeedConfigurationChangeIsAppliedWhileStopped()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        var stoppedConfiguration = Settings(0) with { Stroke = 30, Depth = 55 };
        await Component.SetMotionAsync(stoppedConfiguration, timeout.Token);
        var state = await ReadStateAsync(changes.Reader,
            reported => reported.MotionSettings.Stroke == stoppedConfiguration.Stroke,
            timeout.Token);

        Assert.Equal(stoppedConfiguration.Depth, state.MotionSettings.Depth);
        Assert.Equal(0, state.MotionSettings.Speed);
    }

    [Fact]
    public async Task ZeroSpeedConfigurationChangeIsAppliedWhileMoving()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        var moving = Settings(12);
        await Component.SetMotionAsync(moving, timeout.Token);
        await ReadStateAsync(changes.Reader, state => state.MotionSettings.Speed == moving.Speed, timeout.Token);
        var stoppedConfiguration = moving with { Speed = 0, Stroke = 30, Depth = 55 };

        await Component.SetMotionAsync(stoppedConfiguration, timeout.Token);
        var stopped = await ReadStateAsync(changes.Reader, state => state.MotionSettings.Speed == 0, timeout.Token);

        Assert.Equal(stoppedConfiguration.Stroke, stopped.MotionSettings.Stroke);
        Assert.Equal(stoppedConfiguration.Depth, stopped.MotionSettings.Depth);
    }

    [Fact]
    public async Task NonzeroBufferIsRejected()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(
            changes.Reader,
            _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Component.SetMotionAsync(Settings(12) with { Buffer = 1 }, timeout.Token));

        Assert.Equal("The simulator supports buffer 0 only.", exception.Message);
    }

    [Fact]
    public async Task DisposingAdapterLeavesIndependentSimulatorRunningAndStoppedForNextClient()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        await Component.SetMotionAsync(Settings(12), timeout.Token);

        await Component.DisposeAsync();

        Assert.True(_bridge.IsRunning);
        Assert.Equal(0, await _bridge.ReadSpeedAsync(timeout.Token));
    }

    [Fact]
    public async Task DisposalSettlesExternalAndFaultSendsBeforeReleasingTheConnection()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        await Component.SetDelayAsync(300, timeout.Token);

        var external = Component.InjectExternalAsync(Settings(12), timeout.Token);
        var fault = Component.SetDelayAsync(0, timeout.Token);
        Assert.NotEmpty(GetPending(Component));

        await Component.DisposeAsync();

        Assert.True(external.IsCompleted);
        Assert.True(fault.IsCompleted);
        Assert.True(_bridge.IsRunning);
        Assert.Equal(0, await _bridge.ReadSpeedAsync(timeout.Token));
    }

    [Fact]
    public async Task RealSimulatorCanBeDisposedRepeatedly()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);

        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(
            changes.Reader,
            _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        Drain(changes.Reader);

        await Component.DisposeAsync();
        await Component.DisposeAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.False(changes.Reader.TryRead(out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Component.SetMotionAsync(
            Settings(0),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RealSimulatorReportsExternalSpeedChangeAndUnexpectedSpeedError()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        var external = Settings(25) with { Sensation = 73 };

        await Component.InjectExternalAsync(external, timeout.Token);
        var reported = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Speed == external.Speed,
            timeout.Token);
        var error = await ReadErrorAsync<UnexpectedSpeedChangeException>(changes.Reader, timeout.Token);

        Assert.Equal(external.Sensation, reported.MotionSettings.Sensation);
        Assert.Equal(0, error.PreviousStateSpeed);
        Assert.Equal(external.Speed, error.StateSpeed);
        Assert.Null(error.PendingRequestedSpeed);
        Assert.Equal(reported.Timestamp, error.StateTimestamp);
    }

    [Theory]
    [InlineData("speed")]
    [InlineData("stroke")]
    [InlineData("depth")]
    [InlineData("sensation")]
    [InlineData("stroke-exceeds-depth")]
    [InlineData("nonzero-speed-with-zero-stroke")]
    public async Task ExternalInjectionRejectsInvalidSettings(string scenario)
    {
        var settings = scenario switch
        {
            "speed" => Settings(101),
            "stroke" => Settings(0) with { Stroke = 101, Depth = 101 },
            "depth" => Settings(0) with { Depth = 101 },
            "sensation" => Settings(0) with { Sensation = 101 },
            "stroke-exceeds-depth" => Settings(0) with { Stroke = 41, Depth = 40 },
            "nonzero-speed-with-zero-stroke" => Settings(1) with { Stroke = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            Component.InjectExternalAsync(settings, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalInjectionRequiresSimpleStroke()
    {
        var settings = Settings(0) with { Pattern = MotionPattern.TeasingPounding };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Component.InjectExternalAsync(settings, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public async Task DelayRejectsValuesOutsideProtocolRange(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Component.SetDelayAsync(milliseconds, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task RealSimulatorAcceptsDelayBounds(int milliseconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        await Component.SetDelayAsync(milliseconds, timeout.Token);
    }

    [Fact]
    public async Task RealSimulatorFrozenFeedbackPublishesNoStateUntilUnfrozen()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        await Component.SetStaleAsync(true, timeout.Token);
        Drain(changes.Reader);
        var external = Settings(0) with { Sensation = 73 };
        await Component.InjectExternalAsync(external, timeout.Token);

        Assert.False(changes.Reader.TryRead(out _));

        await Component.SetStaleAsync(false, timeout.Token);
        var reported = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Sensation == external.Sensation,
            timeout.Token);

        Assert.Equal(external.Sensation, reported.MotionSettings.Sensation);
    }

    [Fact]
    public async Task RealSimulatorDisconnectResetsFrozenFeedbackBeforeReconnect()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        var connections = Channel.CreateUnbounded<DeviceConnectionState>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        Component.ConnectionStateChanged += state => connections.Writer.TryWrite(state);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        Drain(connections.Reader);
        await Component.SetStaleAsync(true, timeout.Token);
        Drain(changes.Reader);
        Assert.False(changes.Reader.TryRead(out _));

        await Component.DisconnectAsync(timeout.Token);
        var disconnected = await ReadStateAsync(changes.Reader,
            state => state.MotionSettings.State == MotionDeviceState.Idle,
            timeout.Token);

        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Equal(DeviceConnectionState.Disconnected, await connections.Reader.ReadAsync(timeout.Token));
        Assert.Equal(0, disconnected.MotionSettings.Speed);

        Drain(changes.Reader);
        await Component.ReconnectAsync(timeout.Token);
        var reconnected = await ReadStateAsync(changes.Reader,
            state => state.MotionSettings.State == MotionDeviceState.StrokeEngineIdle,
            timeout.Token);

        Assert.Equal(DeviceConnectionState.Connected, Component.ConnectionState);
        Assert.Equal(DeviceConnectionState.Connected, await connections.Reader.ReadAsync(timeout.Token));
        Assert.Equal(0, reconnected.MotionSettings.Speed);
    }

    [Fact]
    public async Task RealSimulatorDisconnectRejectsWritesAndReconnects()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        var connections = Channel.CreateUnbounded<DeviceConnectionState>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        Component.ConnectionStateChanged += state => connections.Writer.TryWrite(state);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        Drain(connections.Reader);

        await Component.DisconnectAsync(timeout.Token);
        var disconnected = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.State == MotionDeviceState.Idle,
            timeout.Token);
        var inputGate = GetInputGate(Component);
        await inputGate.WaitAsync(timeout.Token);
        Task pendingWrite;
        bool rejectedBeforeSend;
        try
        {
            pendingWrite = Component.SetMotionAsync(Settings(12), timeout.Token);
            rejectedBeforeSend = pendingWrite.IsCompleted && GetPending(Component).IsEmpty;
        }
        finally
        {
            inputGate.Release();
        }

        var rejection = await Assert.ThrowsAsync<IOException>(() => pendingWrite);

        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Equal(DeviceConnectionState.Disconnected, await connections.Reader.ReadAsync(timeout.Token));
        Assert.Equal(0, disconnected.MotionSettings.Speed);
        Assert.True(rejectedBeforeSend);
        Assert.Contains("disconnected", rejection.Message, StringComparison.OrdinalIgnoreCase);

        Drain(changes.Reader);
        await Component.ReconnectAsync(timeout.Token);
        var reconnected = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.State == MotionDeviceState.StrokeEngineIdle,
            timeout.Token);

        Assert.Equal(DeviceConnectionState.Connected, Component.ConnectionState);
        Assert.Equal(DeviceConnectionState.Connected, await connections.Reader.ReadAsync(timeout.Token));
        Assert.Equal(0, reconnected.MotionSettings.Speed);
    }

    [Fact]
    public async Task DisconnectFailsIssuedWriteWithoutStoppingTheSimulator()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        await Component.SetDelayAsync(500, timeout.Token);

        var issuedWrite = Component.SetMotionAsync(Settings(12), timeout.Token);
        try
        {
            Assert.NotEmpty(GetPending(Component));
            await Component.DisconnectAsync(timeout.Token);
            var disconnected = await ReadStateAsync(changes.Reader,
                state => state.MotionSettings.State == MotionDeviceState.Idle, timeout.Token);

            Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
            Assert.Equal(0, disconnected.MotionSettings.Speed);
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                issuedWrite.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token));
            Assert.Contains("unconfirmed", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(GetPending(Component));
        }
        finally
        {
            await Component.StopAsync(timeout.Token);
        }

        Assert.True(_bridge.IsRunning);
        Assert.Equal(0, await _bridge.ReadSpeedAsync(timeout.Token));
    }

    [Fact]
    public async Task QueuedMotionBeforeDisconnectIsRejectedLocally()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        var inputGate = GetInputGate(Component);
        await inputGate.WaitAsync(timeout.Token);
        Task disconnected;
        Task staleWrite;
        int pendingCount;
        try
        {
            staleWrite = Component.SetMotionAsync(Settings(12), timeout.Token);
            disconnected = Component.DisconnectAsync(timeout.Token);
            pendingCount = GetPending(Component).Count;
        }
        finally
        {
            inputGate.Release();
        }

        Assert.Equal(2, pendingCount);
        await disconnected.WaitAsync(timeout.Token);
        var reconnected = Component.ReconnectAsync(timeout.Token);
        await reconnected.WaitAsync(timeout.Token);

        var failure = await Assert.ThrowsAsync<IOException>(() => staleWrite);

        DeviceState? latest = null;
        var observedMotion = false;
        while (changes.Reader.TryRead(out var change))
        {
            if (change.TryPickT0(out var state, out _))
            {
                latest = state;
                observedMotion |= state.MotionSettings.Speed == 12;
            }
        }

        Assert.Contains("connection changed before the request was sent", failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(observedMotion);
        Assert.NotNull(latest);
        Assert.Equal(0, latest.MotionSettings.Speed);
    }

    [Fact]
    public async Task WritesAreRejectedWhileDisconnectIsPendingAndAcceptedAfterReconnect()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);

        var inputGate = GetInputGate(Component);
        await inputGate.WaitAsync(timeout.Token);
        Task disconnected;
        Task writeDuringDisconnect;
        bool rejectedBeforeSend;
        int pendingCount;
        try
        {
            disconnected = Component.DisconnectAsync(timeout.Token);
            writeDuringDisconnect = Component.SetMotionAsync(Settings(12), timeout.Token);
            rejectedBeforeSend = writeDuringDisconnect.IsCompleted && GetPending(Component).Count == 1;
            pendingCount = GetPending(Component).Count;
        }
        finally
        {
            inputGate.Release();
        }

        await disconnected.WaitAsync(timeout.Token);
        var failure = await Assert.ThrowsAsync<IOException>(() => writeDuringDisconnect);
        await Component.ReconnectAsync(timeout.Token);
        await Component.SetMotionAsync(Settings(24), timeout.Token);
        var moved = await ReadStateAsync(changes.Reader,
            state => state.MotionSettings.Speed == 24,
            timeout.Token);

        Assert.Equal(1, pendingCount);
        Assert.True(rejectedBeforeSend);
        Assert.Contains("disconnected", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(24, moved.MotionSettings.Speed);
    }

    [Fact]
    public async Task CancelledDelayedWriteDoesNotPoisonLaterRequest()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);
        await Component.StartAsync(timeout.Token);
        await ReadStateAsync(changes.Reader, _ => Component.ConnectionState == DeviceConnectionState.Connected,
            timeout.Token);
        await Component.SetDelayAsync(250, timeout.Token);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        var delayed = Component.SetMotionAsync(Settings(12), callerCancellation.Token);
        await callerCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delayed);

        Assert.Empty(GetPending(Component));
        await Component.SetMotionAsync(Settings(24), timeout.Token);
        var later = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Speed == 24,
            timeout.Token);
        Assert.Equal(24, later.MotionSettings.Speed);
    }

    [Theory]
    [InlineData("wrong-version")]
    [InlineData("unknown-event")]
    [InlineData("missing-event")]
    [InlineData("event-missing-state")]
    [InlineData("event-with-id")]
    [InlineData("event-with-ok")]
    [InlineData("event-with-error")]
    [InlineData("response-missing-id")]
    [InlineData("response-missing-ok")]
    [InlineData("success-missing-state")]
    [InlineData("success-with-error")]
    [InlineData("failure-missing-error")]
    [InlineData("failure-with-unknown-error")]
    [InlineData("response-with-event")]
    public void MalformedReplyEnvelopeIsRejectedAsInvalidData(string scenario)
    {
        var reply = scenario switch
        {
            "wrong-version" => ValidEvent() with { V = 2 },
            "unknown-event" => ValidEvent() with { Event = "unknown" },
            "missing-event" => ValidEvent() with { Event = null },
            "event-missing-state" => ValidEvent() with { State = null },
            "event-with-id" => ValidEvent() with { Id = 1 },
            "event-with-ok" => ValidEvent() with { Ok = true },
            "event-with-error" => ValidEvent() with { Error = "busy" },
            "response-missing-id" => ValidResponse() with { Id = null },
            "response-missing-ok" => ValidResponse() with { Ok = null },
            "success-missing-state" => ValidResponse() with { State = null },
            "success-with-error" => ValidResponse() with { Error = "busy" },
            "failure-missing-error" => ValidResponse() with { Ok = false, Error = null, State = null },
            "failure-with-unknown-error" => ValidResponse() with { Ok = false, Error = "unknown", State = null },
            "response-with-event" => ValidResponse() with { Event = "state" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<InvalidDataException>(() => Component.ProcessReply(reply));
    }

    [Theory]
    [InlineData("desired-null")]
    [InlineData("sequence-zero")]
    [InlineData("sequence-negative")]
    [InlineData("revision-negative")]
    [InlineData("command-id-zero")]
    [InlineData("command-id-negative")]
    [InlineData("profile-mismatch")]
    [InlineData("position-nan")]
    [InlineData("position-positive-infinity")]
    [InlineData("position-negative-infinity")]
    [InlineData("velocity-nan")]
    [InlineData("velocity-positive-infinity")]
    [InlineData("velocity-negative-infinity")]
    [InlineData("pattern-unsupported")]
    [InlineData("pattern-undefined")]
    [InlineData("speed-negative")]
    [InlineData("speed-too-large")]
    [InlineData("stroke-negative")]
    [InlineData("stroke-too-large")]
    [InlineData("depth-negative")]
    [InlineData("depth-too-large")]
    [InlineData("sensation-negative")]
    [InlineData("sensation-too-large")]
    [InlineData("stroke-exceeds-depth")]
    [InlineData("nonzero-speed-with-zero-stroke")]
    public void MalformedStateIsRejectedAsInvalidData(string scenario)
    {
        var state = InvalidState(scenario);

        Assert.Throws<InvalidDataException>(() =>
            Component.ProcessReply(ValidEvent() with { State = state }));
    }

    [Fact]
    public void MalformedStateIsRejectedBeforeConnectionStateOrSequenceChanges()
    {
        var connectionChanges = new List<DeviceConnectionState>();
        var stateChanges = new List<OneOf<DeviceState, Exception>>();
        Component.ConnectionStateChanged += connectionChanges.Add;
        Component.DeviceStateChanged += stateChanges.Add;
        Component.ProcessReply(ValidEvent());
        var initial = Assert.Single(stateChanges).AsT0;
        Assert.Equal(0, initial.MotionSettings.Speed);
        Assert.Equal([DeviceConnectionState.Connected], connectionChanges);
        connectionChanges.Clear();
        stateChanges.Clear();
        var invalid = ValidState(2) with
        {
            Connected = false,
            Desired = ValidDesired() with { Speed = 101 },
        };

        Assert.Throws<InvalidDataException>(() =>
            Component.ProcessReply(ValidEvent() with { State = invalid }));

        Assert.Equal(DeviceConnectionState.Connected, Component.ConnectionState);
        Assert.Empty(connectionChanges);
        Assert.Empty(stateChanges);

        Component.ProcessReply(ValidEvent() with
        {
            State = ValidState(2),
        });
        var updated = Assert.Single(stateChanges).AsT0;
        Assert.Equal(0, updated.MotionSettings.Speed);
        Assert.Empty(connectionChanges);
    }

    [Fact]
    public void MalformedResponseIsRejectedBeforePendingRequestChanges()
    {
        var connectionChanges = new List<DeviceConnectionState>();
        var stateChanges = new List<OneOf<DeviceState, Exception>>();
        Component.ConnectionStateChanged += connectionChanges.Add;
        Component.DeviceStateChanged += stateChanges.Add;
        var pending = GetPending(Component);
        var completion = new TaskCompletionSource<SimulatorReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(pending.TryAdd(7, completion));

        Assert.Throws<InvalidDataException>(() =>
            Component.ProcessReply(ValidResponse() with { Ok = null }));

        Assert.True(pending.ContainsKey(7));
        Assert.False(completion.Task.IsCompleted);
        Assert.Empty(connectionChanges);
        Assert.Empty(stateChanges);
    }

    [Theory]
    [InlineData("duplicate-envelope")]
    [InlineData("duplicate-state")]
    [InlineData("duplicate-desired")]
    [InlineData("missing-response-error")]
    [InlineData("missing-state-revision")]
    [InlineData("missing-state-command-id")]
    [InlineData("missing-desired-speed")]
    public void DuplicateOrMissingKnownJsonFieldsAreRejected(string scenario)
    {
        var json = scenario switch
        {
            "duplicate-envelope" => ValidEventJson.Replace("\"v\":1", "\"v\":1,\"v\":1", StringComparison.Ordinal),
            "duplicate-state" => ValidEventJson.Replace(
                "\"enabled\":false",
                "\"enabled\":false,\"enabled\":false",
                StringComparison.Ordinal),
            "duplicate-desired" => ValidEventJson.Replace(
                "\"speed\":0",
                "\"speed\":0,\"speed\":0",
                StringComparison.Ordinal),
            "missing-response-error" => ValidResponseJson().Replace(",\"error\":null", string.Empty, StringComparison.Ordinal),
            "missing-state-revision" => ValidEventJson.Replace("\"changeRevision\":0,", string.Empty, StringComparison.Ordinal),
            "missing-state-command-id" => ValidEventJson.Replace("\"commandId\":null,", string.Empty, StringComparison.Ordinal),
            "missing-desired-speed" => ValidEventJson.Replace("\"speed\":0,", string.Empty, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var line = Encoding.UTF8.GetBytes($"{json}\n");

        Assert.Throws<JsonException>(() => ConsumeLines(Component, line));
    }

    [Fact]
    public async Task RejectedResponseStateDoesNotPublishOrConsumeSequence()
    {
        var connectionChanges = new List<DeviceConnectionState>();
        var stateChanges = new List<OneOf<DeviceState, Exception>>();
        Component.ConnectionStateChanged += connectionChanges.Add;
        Component.DeviceStateChanged += stateChanges.Add;
        Component.ProcessReply(ValidEvent());
        Assert.Equal(0, Assert.Single(stateChanges).AsT0.MotionSettings.Speed);
        Assert.Equal([DeviceConnectionState.Connected], connectionChanges);
        connectionChanges.Clear();
        stateChanges.Clear();
        var pending = GetPending(Component);
        var completion = new TaskCompletionSource<SimulatorReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(pending.TryAdd(7, completion));
        var rejection = ValidResponse() with
        {
            Ok = false,
            Error = "disconnected",
            State = ValidState(2) with
            {
                Connected = false,
                Desired = ValidDesired() with { Speed = 20 },
            },
        };

        Component.ProcessReply(rejection);

        Assert.Equal(DeviceConnectionState.Connected, Component.ConnectionState);
        Assert.Empty(connectionChanges);
        Assert.Empty(stateChanges);
        Assert.Same(rejection, await completion.Task);

        Component.ProcessReply(ValidEvent() with
        {
            State = ValidState(2),
        });

        Assert.Equal(0, Assert.Single(stateChanges).AsT0.MotionSettings.Speed);
        Assert.Empty(connectionChanges);
    }

    [Fact]
    public async Task DisconnectFeedbackSettlesOnlyPriorRequests()
    {
        var pending = GetPending(Component);
        var priorWrite = new TaskCompletionSource<SimulatorReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnect = new TaskCompletionSource<SimulatorReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextConnection = new TaskCompletionSource<SimulatorReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(pending.TryAdd(6, priorWrite));
        Assert.True(pending.TryAdd(7, disconnect));
        Assert.True(pending.TryAdd(8, nextConnection));
        var reply = ValidResponse() with { State = ValidState() with { Connected = false } };

        Component.ProcessReply(reply);

        Assert.Same(reply, await disconnect.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            priorWrite.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Contains("unconfirmed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(nextConnection.Task.IsCompleted);

        Component.ProcessReply(ValidEvent() with { State = ValidState(2) with { Connected = false } });

        Assert.False(nextConnection.Task.IsCompleted);
        Assert.True(pending.TryRemove(8, out _));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
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

    private static SimulatorReply ValidEvent() =>
        new(1, null, null, null, "state", ValidState());

    private static SimulatorReply ValidResponse() =>
        new(1, 7, true, null, null, ValidState());

    private static SimulatorState ValidState(long sampleSequence = 1) => new(
        sampleSequence,
        0,
        null,
        ValidDesired(),
        false,
        false,
        0,
        0,
        true,
        "ossm-rs-sim-v1");

    private static SimulatorDesired ValidDesired() => new(0, 0, 10, 20, 50);

    private static string ValidResponseJson() => ValidEventJson.Replace(
        "\"event\":\"state\"",
        "\"id\":7,\"ok\":true,\"error\":null",
        StringComparison.Ordinal);

    private static SimulatorState InvalidState(string scenario)
    {
        var state = ValidState();
        return scenario switch
        {
            "desired-null" => state with { Desired = null! },
            "sequence-zero" => state with { SampleSequence = 0 },
            "sequence-negative" => state with { SampleSequence = -1 },
            "revision-negative" => state with { ChangeRevision = -1 },
            "command-id-zero" => state with { CommandId = 0 },
            "command-id-negative" => state with { CommandId = -1 },
            "profile-mismatch" => state with { ProfileId = "unsupported" },
            "position-nan" => state with { Position = double.NaN },
            "position-positive-infinity" => state with { Position = double.PositiveInfinity },
            "position-negative-infinity" => state with { Position = double.NegativeInfinity },
            "velocity-nan" => state with { Velocity = double.NaN },
            "velocity-positive-infinity" => state with { Velocity = double.PositiveInfinity },
            "velocity-negative-infinity" => state with { Velocity = double.NegativeInfinity },
            "pattern-unsupported" => state with { Desired = ValidDesired() with { Pattern = 1 } },
            "pattern-undefined" => state with { Desired = ValidDesired() with { Pattern = int.MaxValue } },
            "speed-negative" => state with { Desired = ValidDesired() with { Speed = -1 } },
            "speed-too-large" => state with { Desired = ValidDesired() with { Speed = 101 } },
            "stroke-negative" => state with { Desired = ValidDesired() with { Stroke = -1 } },
            "stroke-too-large" => state with { Desired = ValidDesired() with { Stroke = 101, Depth = 101 } },
            "depth-negative" => state with { Desired = ValidDesired() with { Stroke = 0, Depth = -1 } },
            "depth-too-large" => state with { Desired = ValidDesired() with { Depth = 101 } },
            "sensation-negative" => state with { Desired = ValidDesired() with { Sensation = -1 } },
            "sensation-too-large" => state with { Desired = ValidDesired() with { Sensation = 101 } },
            "stroke-exceeds-depth" => state with { Desired = ValidDesired() with { Stroke = 21 } },
            "nonzero-speed-with-zero-stroke" => state with
            {
                Desired = ValidDesired() with { Speed = 1, Stroke = 0 },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    private static ConcurrentDictionary<long, TaskCompletionSource<SimulatorReply>> GetPending(
        SimulatedDevice device)
    {
        var field = typeof(SimulatedDevice).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<ConcurrentDictionary<long, TaskCompletionSource<SimulatorReply>>>(
            field?.GetValue(device));
    }

    private static SemaphoreSlim GetInputGate(SimulatedDevice device)
    {
        var field = typeof(SimulatedDevice).GetField("_inputGate", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SemaphoreSlim>(field?.GetValue(device));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ConsumeLines")]
    private static extern int ConsumeLines(SimulatedDevice device, ReadOnlySpan<byte> buffer);

    private static async Task<DeviceState> ReadStateAsync(
        ChannelReader<OneOf<DeviceState, Exception>> reader,
        Func<DeviceState, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var change = await reader.ReadAsync(cancellationToken);
            if (change.TryPickT0(out var state, out _) && predicate(state))
            {
                return state;
            }

            if (change.IsT1)
            {
                throw change.AsT1;
            }
        }
    }

    private static async Task<TException> ReadErrorAsync<TException>(
        ChannelReader<OneOf<DeviceState, Exception>> reader,
        CancellationToken cancellationToken)
        where TException : Exception
    {
        while (true)
        {
            var change = await reader.ReadAsync(cancellationToken);
            if (change.TryPickT1(out var error, out _))
            {
                return Assert.IsType<TException>(error);
            }
        }
    }

    private static void Drain<T>(ChannelReader<T> reader)
    {
        while (reader.TryRead(out _))
        {
        }
    }

    private static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(SimulatedDeviceTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");
        return path;
    }
}
