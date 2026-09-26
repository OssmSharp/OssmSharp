using System.Reflection;
using System.Text.Json;

namespace OssmSharp.Cli.IntegrationTests;

/// <summary>
/// Exercises command runner output and failures against a real simulator host.
/// </summary>
public sealed class DeviceCommandRunnerTests
{
    [Fact]
    public async Task StartReportsTheNativeSimulatorState()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StringWriter();
        var endpoint = SimulatorEndpoint.CreateUnique();
        Task<int>? host = null;

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Start, SimulatorPath()),
            output,
            TextWriter.Null,
            endpoint,
            (path, value) => SimulatorHostLaunch.Create(host = IsolatedSimulatorHost.RunAsync(path, value, timeout.Token)),
            timeout.Token);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Equal("Connected", document.RootElement.GetProperty("connectionState").GetString());
        Assert.Equal("ossm-rs-sim-v1", document.RootElement.GetProperty("device").GetProperty("profileId").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("state").GetProperty("motionSettings").GetProperty("speed").GetInt32());

        await SimulatorClient.SendAsync(endpoint, new SimulatorIpcRequest("stop"), timeout.Token);
        Assert.Equal(0, await host!);
    }

    [Fact]
    public async Task MoveReportsAppliedMotionAndLeavesTheHostRunning()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StringWriter();
        var endpoint = SimulatorEndpoint.CreateUnique();
        Task<int>? host = null;

        Assert.Equal(0, await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Start, SimulatorPath()),
            TextWriter.Null,
            TextWriter.Null,
            endpoint,
            (path, value) => SimulatorHostLaunch.Create(host = IsolatedSimulatorHost.RunAsync(path, value, timeout.Token)),
            timeout.Token));

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(
                DeviceCommandOperation.Move,
                Speed: 12,
                Stroke: 20,
                Depth: 50,
                Duration: TimeSpan.FromMilliseconds(100)),
            output,
            TextWriter.Null,
            endpoint,
            (_, _) => throw new InvalidOperationException("The running host must be reused."),
            timeout.Token);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Equal(12, document.RootElement.GetProperty("state").GetProperty("motionSettings").GetProperty("speed").GetInt32());

        await SimulatorClient.SendAsync(endpoint, new SimulatorIpcRequest("stop"), timeout.Token);
        Assert.Equal(0, await host!);
    }

    [Fact]
    public async Task StartReturnsFailureWhenTheExecutableIsNotTheSimulator()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var endpoint = SimulatorEndpoint.CreateUnique();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Start, NonSimulatorPath()),
            output,
            error,
            endpoint,
            (path, value) => SimulatorHostLaunch.Create(IsolatedSimulatorHost.RunAsync(path, value, timeout.Token)),
            timeout.Token);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    private static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(DeviceCommandRunnerTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var fileName = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", fileName);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");
        return path;
    }

    private static string NonSimulatorPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
        : "/usr/bin/false";
}
