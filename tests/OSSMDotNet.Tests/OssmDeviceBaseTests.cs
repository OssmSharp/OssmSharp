using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OneOf;

namespace OSSMDotNet.Tests;

public sealed class OssmDeviceBaseTests
{
    private static readonly DeviceInformation SimulatorInformation = new(
        "simulator",
        "ossm-rs-sim-v1",
        null,
        [MotionPattern.SimpleStroke]);

    [Fact]
    public async Task StartsUnavailableThenPublishesConnectedDeviceFacts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time);
        var changes = Channel.CreateUnbounded<OneOf<DeviceSnapshot, Exception>>();
        device.DeviceChanged += change => changes.Writer.TryWrite(change);

        var initial = await device.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await device.StartAsync(TestContext.Current.CancellationToken);
        var connected = await ReadSnapshotAsync(changes.Reader, snapshot => snapshot.ConnectionState == DeviceConnectionState.Connected);

        Assert.Equal(DeviceConnectionState.Disconnected, initial.ConnectionState);
        Assert.Equal(ObservationFreshness.Unavailable, initial.ObservationFreshness);
        Assert.Null(initial.Observation);
        Assert.Equal(SimulatorInformation, connected.Device);
    }

    [Fact]
    public async Task ObservationAgesToLastKnownWithoutChangingItsTimestamp()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time);
        var changes = Channel.CreateUnbounded<OneOf<DeviceSnapshot, Exception>>();
        device.DeviceChanged += change => changes.Writer.TryWrite(change);
        await device.StartAsync(TestContext.Current.CancellationToken);
        var observedAt = time.GetUtcNow().UtcDateTime;
        device.Observe(Settings(speed: 10), observedAt);
        await ReadSnapshotAsync(changes.Reader, snapshot => snapshot.ObservationFreshness == ObservationFreshness.Live);

        time.Advance(TimeSpan.FromSeconds(3));
        var lastKnown = await ReadSnapshotAsync(changes.Reader, snapshot => snapshot.ObservationFreshness == ObservationFreshness.LastKnown);

        Assert.Equal(observedAt, lastKnown.Observation?.Timestamp);
        Assert.Equal(10, lastKnown.Observation?.MotionSettings.Speed);
    }

    [Fact]
    public async Task RegistersPendingWriteBeforeDispatchAndDoesNotReportItsSynchronousObservation()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time) { ObserveDuringDispatch = true };
        var errors = Channel.CreateUnbounded<Exception>();
        device.DeviceChanged += change => change.Switch(_ => { }, error => errors.Writer.TryWrite(error));
        await device.StartAsync(TestContext.Current.CancellationToken);

        await device.SetMotionAsync(Settings(speed: 30), TestContext.Current.CancellationToken);

        Assert.False(errors.Reader.TryRead(out _));
        Assert.Equal(30, (await device.GetSnapshotAsync(TestContext.Current.CancellationToken)).RequestedMotion?.Speed);
    }

    [Fact]
    public async Task UnexpectedSpeedCommitsSnapshotBeforePublishingTheException()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time);
        var report = new TaskCompletionSource<(UnexpectedSpeedChangeException Error, DeviceSnapshot Snapshot)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.DeviceChanged += change => change.Switch(
            _ => { },
            error =>
            {
                var unexpected = Assert.IsType<UnexpectedSpeedChangeException>(error);
                var snapshot = device.GetSnapshotAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                report.TrySetResult((unexpected, snapshot));
            });
        await device.StartAsync(TestContext.Current.CancellationToken);
        device.Observe(Settings(speed: 10), time.GetUtcNow().UtcDateTime);

        device.Observe(Settings(speed: 20), time.GetUtcNow().UtcDateTime);
        var reported = await report.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20, reported.Snapshot.Observation?.MotionSettings.Speed);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotPreventLaterSubscribersOrBecomeADeviceError()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time);
        var laterSubscriberCalls = 0;
        var reportedErrors = 0;
        device.DeviceChanged += _ => throw new InvalidOperationException("subscriber failure");
        device.DeviceChanged += change => change.Switch(
            _ => Interlocked.Increment(ref laterSubscriberCalls),
            _ => Interlocked.Increment(ref reportedErrors));

        await device.StartAsync(TestContext.Current.CancellationToken);
        device.Observe(Settings(speed: 0), time.GetUtcNow().UtcDateTime);

        Assert.True(laterSubscriberCalls >= 2);
        Assert.Equal(0, reportedErrors);
    }

    [Fact]
    public async Task InvalidSettingsAndPredispatchCancellationNeverReachTransportOrFaultEvent()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time);
        var reportedErrors = 0;
        device.DeviceChanged += change => change.Switch(_ => { }, _ => reportedErrors++);
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
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(time) { BlockDispatch = true };
        await device.StartAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        var write = device.SetMotionAsync(Settings(speed: 30), cancellation.Token);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        Assert.False(write.IsCompleted);
        device.CompleteDispatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(
            30,
            (await device.GetSnapshotAsync(TestContext.Current.CancellationToken)).RequestedMotion?.Speed);
    }

    [Fact]
    public async Task DisposalWaitsForIssuedDispatchBeforeReleasingTransportResources()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var device = new TestDevice(time) { BlockDispatch = true };
        await device.StartAsync(TestContext.Current.CancellationToken);
        var write = device.SetMotionAsync(Settings(speed: 30), TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            device.CoreDisposed.WaitAsync(
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken));

        device.CompleteDispatch();
        await write;
        await disposal;
        Assert.True(device.CoreDisposed.IsCompletedSuccessfully);
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

    private static async Task<DeviceSnapshot> ReadSnapshotAsync(
        ChannelReader<OneOf<DeviceSnapshot, Exception>> reader,
        Func<DeviceSnapshot, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (await reader.ReadAsync(timeout.Token) is { } change)
        {
            if (change.TryPickT0(out var snapshot, out _) && predicate(snapshot))
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("Snapshot channel completed unexpectedly.");
    }

    private sealed class TestDevice(FakeTimeProvider timeProvider)
        : OssmDeviceBase(
            new OssmDeviceOptions
            {
                ObservationLiveFor = TimeSpan.FromSeconds(2),
                ExpectationLifetime = TimeSpan.FromSeconds(1),
            },
            timeProvider,
            NullLogger.Instance)
    {
        private readonly TaskCompletionSource _dispatchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockDispatch { get; init; }

        public bool ObserveDuringDispatch { get; init; }

        public Task DispatchStarted => _dispatchStarted.Task;

        public Task CoreDisposed => _coreDisposed.Task;

        public int DispatchCount { get; private set; }

        private readonly TaskCompletionSource _dispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _coreDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteDispatch() => _dispatchCompleted.TrySetResult();

        public void Observe(MotionSettings settings, DateTime timestamp) =>
            PublishObservation(new DeviceState(timestamp, settings, Guid.Empty), null, null);

        protected override async Task RunTransportAsync(CancellationToken cancellationToken)
        {
            PublishConnected(SimulatorInformation);
            using var registration = cancellationToken.Register(() => _stopped.TrySetResult());
            await _stopped.Task;
        }

        protected override async Task DispatchMotionAsync(MotionSettings motionSettings, CancellationToken cancellationToken)
        {
            DispatchCount++;
            _dispatchStarted.TrySetResult();
            if (ObserveDuringDispatch)
            {
                PublishObservation(
                    new DeviceState(timeProvider.GetUtcNow().UtcDateTime, motionSettings, Guid.Empty),
                    null,
                    true);
            }

            if (BlockDispatch)
            {
                await _dispatchCompleted.Task;
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            _coreDisposed.TrySetResult();
            await base.DisposeCoreAsync();
        }
    }
}
