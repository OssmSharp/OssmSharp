using System.Reflection;

namespace OssmSharp.Cli.IntegrationTests;

/// <summary>
/// Verifies host persistence, motion limits, and lifecycle commands across simulator IPC requests.
/// </summary>
public sealed class PersistentSimulatorHostTests
{
    [Fact]
    public async Task HostPersistsAcrossCommandsAndStopsAtZero()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var endpoint = SimulatorEndpoint.CreateUnique();
        var host = IsolatedSimulatorHost.RunAsync(SimulatorPath(), endpoint, timeout.Token);

        try
        {
            var started = await SendWhenReadyAsync(
                endpoint,
                new SimulatorIpcRequest("start", SimulatorPath()),
                timeout.Token);
            var moved = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest("move", Speed: 12, Stroke: 20, Depth: 50, DurationMilliseconds: 200),
                timeout.Token);
            var moving = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest("status"),
                timeout.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token);
            var settled = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest("status"),
                timeout.Token);
            var stopped = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest("stop"),
                timeout.Token);

            Assert.True(started.Ok, started.Error);
            Assert.True(moved.Ok, moved.Error);
            Assert.Equal(12, moved.Output!.State.MotionSettings.Speed);
            Assert.Equal(12, moving.Output!.State.MotionSettings.Speed);
            Assert.Equal(0, settled.Output!.State.MotionSettings.Speed);
            Assert.Equal(0, stopped.Output!.State.MotionSettings.Speed);
            Assert.Equal(0, await host);
        }
        finally
        {
            await timeout.CancelAsync();
            await IgnoreCancellationAsync(host);
        }
    }

    [Fact]
    public async Task StartRejectsAPathDifferentFromTheRunningHost()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var endpoint = SimulatorEndpoint.CreateUnique();
        var host = IsolatedSimulatorHost.RunAsync(SimulatorPath(), endpoint, timeout.Token);

        try
        {
            await SendWhenReadyAsync(endpoint, new SimulatorIpcRequest("status"), timeout.Token);

            var response = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest("start", NonSimulatorPath()),
                timeout.Token);

            Assert.False(response.Ok);
            Assert.Equal("A simulator host is already running with a different executable.", response.Error);
        }
        finally
        {
            await SimulatorClient.SendAsync(endpoint, new SimulatorIpcRequest("stop"), timeout.Token);
            Assert.Equal(0, await host);
        }
    }

    [Fact]
    public async Task HostRejectsOutOfBoundsMotionWithoutChangingState()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var endpoint = SimulatorEndpoint.CreateUnique();
        var host = IsolatedSimulatorHost.RunAsync(SimulatorPath(), endpoint, timeout.Token);

        try
        {
            await SendWhenReadyAsync(endpoint, new SimulatorIpcRequest("status"), timeout.Token);

            var rejected = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest(
                    "move",
                    Speed: 101,
                    Stroke: 20,
                    Depth: 50,
                    DurationMilliseconds: 100),
                timeout.Token);
            var status = await SimulatorClient.SendAsync(
                endpoint,
                new SimulatorIpcRequest("status"),
                timeout.Token);

            Assert.False(rejected.Ok);
            Assert.Equal(0, status.Output!.State.MotionSettings.Speed);
        }
        finally
        {
            await SimulatorClient.SendAsync(endpoint, new SimulatorIpcRequest("stop"), timeout.Token);
            Assert.Equal(0, await host);
        }
    }

    [Fact]
    public async Task RunnerReportsUnavailableHostWithoutStartingOneForStatus()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Status),
            output,
            error,
            SimulatorEndpoint.CreateUnique(),
            (_, _) => throw new InvalidOperationException("Status must not launch a simulator host."),
            timeout.Token);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("not running", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SimulatorIpcResponse> SendWhenReadyAsync(
        SimulatorEndpoint endpoint,
        SimulatorIpcRequest request,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetTimestamp() + TimeProvider.System.TimestampFrequency * 5;
        while (true)
        {
            try
            {
                return await SimulatorClient.SendAsync(endpoint, request, cancellationToken);
            }
            catch (IOException) when (TimeProvider.System.GetTimestamp() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
            catch (TimeoutException) when (TimeProvider.System.GetTimestamp() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(PersistentSimulatorHostTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");
        return path;
    }

    private static string NonSimulatorPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
        : "/usr/bin/false";
}
