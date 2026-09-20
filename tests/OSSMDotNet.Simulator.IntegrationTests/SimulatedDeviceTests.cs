using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneOf;

namespace OSSMDotNet.Simulator.IntegrationTests;

public sealed class SimulatedDeviceTests
{
    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new SimulatedDeviceOptions { ExecutablePath = "relative-path" };

        Assert.IsAssignableFrom<OssmDeviceBaseOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        Assert.Throws<ValidationException>(() => new SimulatedDevice(
            Options.Create(options),
            TimeProvider.System,
            NullLogger<SimulatedDevice>.Instance));
    }

    [Fact]
    public async Task RealSimulatorReportsMotionAndSettledZeroSpeedWrite()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var device = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = SimulatorPath() }),
            TimeProvider.System,
            NullLogger<SimulatedDevice>.Instance);
        var changes = Channel.CreateUnbounded<OneOf<DeviceSnapshot, Exception>>();
        device.DeviceChanged += change => changes.Writer.TryWrite(change);

        await device.StartAsync(timeout.Token);
        var initial = await ReadSnapshotAsync(
            changes.Reader,
            snapshot => snapshot.ConnectionState == DeviceConnectionState.Connected &&
                snapshot.ObservationFreshness == ObservationFreshness.Live,
            timeout.Token);
        var moving = Settings(12);

        await device.SetMotionAsync(moving, timeout.Token);
        var applied = await ReadSnapshotAsync(
            changes.Reader,
            snapshot => snapshot.Observation?.MotionSettings.Speed == moving.Speed &&
                snapshot.RequestedMotion == moving,
            timeout.Token);
        await device.SetMotionAsync(moving with { Speed = 0 }, timeout.Token);
        var stopped = await ReadSnapshotAsync(
            changes.Reader,
            snapshot => snapshot.Observation?.MotionSettings.Speed == 0 &&
                snapshot.RequestedMotion?.Speed == 0,
            timeout.Token);

        Assert.Equal("ossm-rs simulator", initial.Device?.Identity);
        Assert.Equal("ossm-rs-sim-v1", initial.Device?.ProfileId);
        Assert.Equal(moving, applied.RequestedMotion);
        Assert.Equal(moving.Stroke, stopped.Observation?.MotionSettings.Stroke);
        Assert.Equal(moving.Depth, stopped.Observation?.MotionSettings.Depth);
        Assert.Equal(moving.Pattern, stopped.Observation?.MotionSettings.Pattern);
    }

    [Fact]
    public async Task RealSimulatorCanBeDisposedRepeatedly()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var device = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = SimulatorPath() }),
            TimeProvider.System,
            NullLogger<SimulatedDevice>.Instance);
        var changes = Channel.CreateUnbounded<OneOf<DeviceSnapshot, Exception>>();
        device.DeviceChanged += change => changes.Writer.TryWrite(change);

        try
        {
            await device.StartAsync(timeout.Token);
            await ReadSnapshotAsync(
                changes.Reader,
                snapshot => snapshot.ConnectionState == DeviceConnectionState.Connected,
                timeout.Token);

            await device.DisposeAsync();
            await device.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                device.GetSnapshotAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await device.DisposeAsync();
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

    private static async Task<DeviceSnapshot> ReadSnapshotAsync(
        ChannelReader<OneOf<DeviceSnapshot, Exception>> reader,
        Func<DeviceSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var change = await reader.ReadAsync(cancellationToken);
            if (change.TryPickT0(out var snapshot, out var exception))
            {
                if (predicate(snapshot))
                {
                    return snapshot;
                }
            }
            else
            {
                throw exception;
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
