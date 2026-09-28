using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Simulator;
using OssmSharp.Device.Simulator.Models;

namespace OssmSharp.Cli.IntegrationTests;

[Collection("NativeSimulatorProcessTests")]
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
            await device.InitializeAsync(timeout.Token);
            await device.SetAsync(settings, timeout.Token);
            Assert.Equal(12, (await moving.Task.WaitAsync(timeout.Token)).MotionSettings.Speed);

            await device.EmergencyStopAsync(timeout.Token);
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

    [Fact]
    public async Task FailedReadinessReapsTheChildBeforeReportingFailure()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/usr/bin/yes";
        using var bridge = new SimulatorBackgroundService(executable);

        try
        {
            await bridge.StartAsync(timeout.Token);
            await Assert.ThrowsAsync<IOException>(() => bridge.WaitUntilReadyAsync(timeout.Token));
            var pid = OwnedProcessId(bridge);
            Assert.False(IsRunning(pid), "A child that rejects the readiness handshake must not be orphaned.");
        }
        finally
        {
            await bridge.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task TimedOutReadinessReapsTheChildBeforeReportingTimeout()
    {
        if (OperatingSystem.IsWindows())
            return; // The native Windows fixture does not provide a silent startup mode.

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var silentExecutable = Path.Combine(Path.GetTempPath(), $"ossm-sim-no-ready-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(silentExecutable, "#!/bin/sh\nexec /bin/sleep 60\n", timeout.Token);
        File.SetUnixFileMode(
            silentExecutable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var bridge = new SimulatorBackgroundService(silentExecutable);

        try
        {
            await bridge.StartAsync(timeout.Token);
            await Assert.ThrowsAsync<TimeoutException>(() => bridge.WaitUntilReadyAsync(timeout.Token));
            var pid = OwnedProcessId(bridge);
            Assert.False(IsRunning(pid), "A timed-out startup must not leave its child running.");
        }
        finally
        {
            try
            {
                await bridge.StopAsync(timeout.Token);
            }
            finally
            {
                File.Delete(silentExecutable);
            }
        }
    }

    [Fact]
    public async Task CancelledStopStillAwaitsTheOwnedSimulatorExit()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var bridge = new SimulatorBackgroundService(SimulatorPath());
        int? processId = null;

        try
        {
            await bridge.StartAsync(timeout.Token);
            await bridge.WaitUntilReadyAsync(timeout.Token);
            processId = OwnedProcessId(bridge);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await bridge.StopAsync(cancelled.Token);

            Assert.False(IsRunning(processId.Value), "Cancellation does not establish that the child exited.");
            Assert.True(bridge.ExecuteTask?.IsCompleted);
        }
        finally
        {
            if (processId is { } pid && IsRunning(pid))
            {
                using var orphan = Process.GetProcessById(pid);
                orphan.Kill(entireProcessTree: true);
                await orphan.WaitForExitAsync(timeout.Token);
            }
        }
    }

    [Fact]
    public async Task StopDuringProcessStartCannotReportSettledCleanup()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var startGate = new ManualResetEventSlim();
        var enteringStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int? processId = null;
        using var bridge = new SimulatorBackgroundService(SimulatorPath(), start =>
        {
            enteringStart.TrySetResult();
            startGate.Wait();
            var process = Process.Start(start);
            processId = process?.Id;
            return process;
        });

        await bridge.StartAsync(timeout.Token);
        await enteringStart.Task.WaitAsync(timeout.Token);

        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                bridge.StopAsync(CancellationToken.None).WaitAsync(timeout.Token));
            Assert.Contains("did not exit", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            startGate.Set();
            try
            {
                if (bridge.ExecuteTask is { } execution)
                    await execution.WaitAsync(timeout.Token);
            }
            finally
            {
                await bridge.StopAsync(timeout.Token);
            }
        }

        Assert.True(processId is { } pid && !IsRunning(pid), "Startup cleanup must reap the child.");
    }

    internal static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(SimulatorPromptIntegrationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");
        return path;
    }

    private static int OwnedProcessId(SimulatorBackgroundService bridge)
    {
        var child = typeof(SimulatorBackgroundService)
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(bridge);
        return Assert.IsType<Process>(child).Id;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
