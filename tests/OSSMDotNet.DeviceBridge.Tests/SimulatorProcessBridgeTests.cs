using System.Threading.Channels;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OSSMDotNet.DeviceBridge;
using OSSMDotNet.Models;

namespace OSSMDotNet.DeviceBridge.Tests;

public sealed class SimulatorProcessBridgeTests
{
    [Fact]
    public async Task RealRustBridgeReportsWritesExternalChangesAndSettledStop()
    {
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        var samples = Channel.CreateBounded<DeviceSample>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
        bridge.SampleReceived += sample => samples.Writer.TryWrite(sample);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        bridge.Start(SimulatorPath());
        var initial = await ReadUntil(samples.Reader, sample => sample.Connected, deadline.Token);
        Assert.Equal("ossm-rs-sim-v1", initial.ProfileId);
        Assert.False(initial.Enabled);
        var desired = new DesiredState(0, 12, 10, 20, 50);

        await bridge.WriteAsync(1, desired, deadline.Token);
        var applied = await ReadUntil(samples.Reader, sample => sample.CommandId == 1 && sample.Desired == desired, deadline.Token);
        Assert.True(applied.Enabled);
        await bridge.InjectAsync("external", desired with { Speed = 0 });
        var external = await ReadUntil(samples.Reader,
            sample => sample.ChangeRevision > applied.ChangeRevision && sample.CommandId is null, deadline.Token);
        Assert.Equal(0, external.Desired.Speed);

        await bridge.StopAsync(2, deadline.Token);
        var stopped = await ReadUntil(samples.Reader,
            sample => sample.CommandId == 2 && !sample.Enabled && !sample.Moving, deadline.Token);
        Assert.Equal(0, stopped.Desired.Speed);
        Assert.Null(bridge.Failure);
    }

    [Fact]
    public async Task CancellingAfterDispatchMustNotReportCompletionBeforeTheRustWriteSettles()
    {
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        bridge.Start(SimulatorPath());
        await bridge.InjectAsync("delay", value: 300);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var write = bridge.WriteAsync(1, new(0, 12, 10, 20, 50), cancellation.Token);

        // This response confirms the simulator received the earlier delayed write.
        await bridge.InjectAsync("external", new(0, 0, 10, 20, 50));
        await cancellation.CancelAsync();

        Assert.False(write.IsCompleted, "An issued write must stay owned until its actual completion, despite caller cancellation.");
        await write.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await bridge.StopAsync(2, TestContext.Current.CancellationToken);
    }

    private static async Task<DeviceSample> ReadUntil(ChannelReader<DeviceSample> reader, Func<DeviceSample, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            var sample = await reader.ReadAsync(cancellationToken);
            if (predicate(sample)) return sample;
        }
    }

    private static string SimulatorPath()
    {
        var root = Path.GetFullPath(typeof(SimulatorProcessBridgeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the Rust simulator adapter with cargo build --release --locked before these integration tests.");
        return path;
    }
}
