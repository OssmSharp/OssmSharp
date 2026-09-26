using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Simulator;
using OssmSharp.Device.Simulator.Models;

namespace OssmSharp.Cli.IntegrationTests;

public sealed class SimulatorPromptIntegrationTests
{
    [Fact]
    public async Task SimulatorSessionWritesMotionAndStopsBeforeTheOwnedChildExits()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var bridge = new SimulatorBackgroundService(SimulatorPath());

        try
        {
            await bridge.StartAsync(timeout.Token);
            await bridge.WaitUntilReadyAsync(timeout.Token);
            await using var device = new SimulatedDevice(
                Options.Create(new SimulatedDeviceOptions { Endpoint = bridge.Endpoint }),
                TimeProvider.System, NullLogger<SimulatedDevice>.Instance);
            var initial = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var moving = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.DeviceStateChanged += change =>
            {
                if (!change.IsT0)
                    return;

                var report = change.AsT0;
                initial.TrySetResult(report);
                if (report.MotionSettings.Speed == 12)
                    moving.TrySetResult(report);
                if (moving.Task.IsCompleted && report.MotionSettings.Speed == 0)
                    stopped.TrySetResult(report);
            };

            await device.StartAsync(timeout.Token);
            var baseline = await initial.Task.WaitAsync(timeout.Token);
            var settings = baseline.MotionSettings with
            {
                State = MotionDeviceState.StrokeEngineIdle,
                Speed = 12,
                Stroke = 20,
                Depth = 50,
                Sensation = 50,
                Buffer = 0,
                Pattern = MotionPattern.SimpleStroke,
            };
            await device.SetMotionAsync(settings, timeout.Token);
            Assert.Equal(12, (await moving.Task.WaitAsync(timeout.Token)).MotionSettings.Speed);

            await device.SetMotionAsync(settings with { Speed = 0 }, timeout.Token);
            Assert.Equal(0, (await stopped.Task.WaitAsync(timeout.Token)).MotionSettings.Speed);
            await device.DisposeAsync();
            Assert.False(bridge.ExecuteTask?.IsCompleted);
        }
        finally
        {
            await bridge.StopAsync(timeout.Token);
        }

        Assert.True(bridge.ExecuteTask?.IsCompleted);
    }

    [Fact]
    public async Task InvalidSimulatorExecutableNeverReportsReady()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/usr/bin/false";
        using var bridge = new SimulatorBackgroundService(executable);

        try
        {
            await bridge.StartAsync(timeout.Token);
            await Assert.ThrowsAsync<IOException>(() => bridge.WaitUntilReadyAsync(timeout.Token));
        }
        finally
        {
            await bridge.StopAsync(timeout.Token);
        }
    }

    private static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(SimulatorPromptIntegrationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");
        return path;
    }
}
