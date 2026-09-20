using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using OneOf;

namespace OSSMDotNet.Simulator.IntegrationTests;

public sealed class SimulatedDeviceTests : MockerTestBase<SimulatedDevice>, IAsyncDisposable
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.SetupOptions(new SimulatedDeviceOptions { ExecutablePath = SimulatorPath() });
        mocks.AddType<TimeProvider>(TimeProvider.System);
    };

    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new SimulatedDeviceOptions { ExecutablePath = "relative-path" };
        Mocks.SetupOptions(options, replace: true);

        Assert.IsAssignableFrom<OssmDeviceBaseOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        var exception = Assert.Throws<TargetInvocationException>(() => Mocks.CreateInstance<SimulatedDevice>());
        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public async Task RealSimulatorReportsMotionAndSettledZeroSpeedWrite()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);

        var beforeStartup = Component.GetStateAsync(timeout.Token);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => beforeStartup);
        Assert.False(changes.Reader.TryRead(out _));
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => Component.Device);

        await Component.StartAsync(timeout.Token);
        var initial = await ReadStateAsync(
            changes.Reader,
            state => Component.ConnectionState == DeviceConnectionState.Connected &&
                state.ObservationFreshness == ObservationFreshness.Live,
            timeout.Token);
        var moving = Settings(12);

        await Component.SetMotionAsync(moving, timeout.Token);
        var applied = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Speed == moving.Speed &&
                state.RequestedMotion == moving,
            timeout.Token);
        await Component.SetMotionAsync(moving with { Speed = 0 }, timeout.Token);
        var stopped = await ReadStateAsync(
            changes.Reader,
            state => state.MotionSettings.Speed == 0 &&
                state.RequestedMotion?.Speed == 0,
            timeout.Token);
        var current = await Component.GetStateAsync(timeout.Token);

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
        Assert.Null(initial.RequestedMotion);
        Assert.Equal(ObservationFreshness.Live, applied.ObservationFreshness);
        Assert.Equal(ObservationFreshness.Live, stopped.ObservationFreshness);
        Assert.Equal(moving, applied.RequestedMotion);
        Assert.Equal(moving.Stroke, stopped.MotionSettings.Stroke);
        Assert.Equal(moving.Depth, stopped.MotionSettings.Depth);
        Assert.Equal(moving.Pattern, stopped.MotionSettings.Pattern);
        Assert.Equal(0, current.MotionSettings.Speed);
        Assert.Equal(moving with { Speed = 0 }, current.RequestedMotion);
        Assert.Equal(stopped.SessionId, current.SessionId);

        var information = Component.Device;
        await Component.StopAsync(timeout.Token);
        var lastObservation = stopped;
        while (changes.Reader.TryRead(out var change))
        {
            if (change.IsT1)
            {
                throw change.AsT1;
            }

            lastObservation = change.AsT0;
        }

        var retained = await Component.GetStateAsync(timeout.Token);
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Same(information, Component.Device);
        Assert.Equal(lastObservation, retained);
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
            state => Component.ConnectionState == DeviceConnectionState.Connected &&
                state.ObservationFreshness == ObservationFreshness.Live,
            timeout.Token);

        await Component.DisposeAsync();
        await Component.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            Component.GetStateAsync(TestContext.Current.CancellationToken));
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
