using System.Text.Json;
using System.Reflection;

namespace OSSMDotNet.DeviceCli.IntegrationTests;

public sealed class DeviceCommandRunnerTests
{
    [Fact]
    public async Task StatusReportsTheNativeSimulatorState()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StringWriter();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Status, SimulatorPath()),
            output,
            timeout.Token);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Equal("Connected", document.RootElement.GetProperty("connectionState").GetString());
        Assert.Equal("ossm-rs-sim-v1", document.RootElement.GetProperty("device").GetProperty("profileId").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("state").GetProperty("motionSettings").GetProperty("speed").GetInt32());
    }

    [Fact]
    public async Task MoveReportsAppliedMotionAndReturnsAfterTheBoundedDuration()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StringWriter();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(
                DeviceCommandOperation.Move,
                SimulatorPath(),
                12,
                20,
                50,
                TimeSpan.FromMilliseconds(100)),
            output,
            timeout.Token);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Equal(12, document.RootElement.GetProperty("state").GetProperty("motionSettings").GetProperty("speed").GetInt32());
    }

    [Fact]
    public async Task StatusReturnsFailureWhenTheExecutableIsNotTheSimulator()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var output = new StringWriter();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Status, NonSimulatorPath()),
            output,
            timeout.Token);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
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
