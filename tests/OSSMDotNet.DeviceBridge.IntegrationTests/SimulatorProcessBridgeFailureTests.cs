using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OSSMDotNet.DeviceBridge;

namespace OSSMDotNet.DeviceBridge.IntegrationTests;

public sealed class SimulatorProcessBridgeFailureTests
{
    [Fact]
    public async Task MissingExecutableFailsBeforeOwnershipAndAllowsASubsequentLocalStart()
    {
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        var missing = Path.Combine(RepositoryRoot(), "tools", "ossm-sim-bridge", "target", "release",
            "missing-ossm-sim-bridge.exe");

        var exception = Assert.Throws<FileNotFoundException>(() => bridge.Start(missing));

        Assert.Contains("ossm-sim-bridge executable", exception.Message, StringComparison.Ordinal);
        Assert.Null(bridge.Failure);
        bridge.Start(SimulatorPath());
        await bridge.StopAsync(1, TestContext.Current.CancellationToken);
        Assert.Null(bridge.Failure);
    }

    [Fact]
    public async Task OperationBeforeStartIsRejectedWithoutCreatingAFailure()
    {
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            bridge.SetMotionAsync(1, MotionSettings.Stopped, TestContext.Current.CancellationToken));

        Assert.Equal("simulator-unavailable", exception.Message);
        Assert.Null(bridge.Failure);
        Assert.False(bridge.HasDiagnostics);
    }

    [Fact]
    public async Task StartingTwiceRejectsTheSecondStartWhileTheOwnedProcessRemainsUsable()
    {
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        bridge.Start(SimulatorPath());

        var exception = Assert.Throws<InvalidOperationException>(() => bridge.Start(SimulatorPath()));

        Assert.Equal("simulator-already-started", exception.Message);
        await bridge.StopAsync(2, TestContext.Current.CancellationToken);
        Assert.Null(bridge.Failure);
    }

    [Fact]
    public async Task SendAfterDisposalRejectsAndRepeatedDisposalIsSafe()
    {
        var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        await bridge.DisposeAsync();

        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            bridge.StopAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(typeof(SimulatorProcessBridge).FullName, exception.ObjectName);
        Assert.Null(bridge.Failure);
        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentStopIsRejectedAsBusyWithoutCompletingTheDelayedOwnedWrite()
    {
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        bridge.Start(SimulatorPath());
        await bridge.InjectAsync("delay", value: 1800);
        var delayedWrite = bridge.SetMotionAsync(10, new(0, 12, 10, 20, 50), TestContext.Current.CancellationToken);

        var busy = await Assert.ThrowsAsync<IOException>(() =>
            bridge.StopAsync(11, TestContext.Current.CancellationToken));

        Assert.Equal("simulator-rejected:busy", busy.Message);
        Assert.False(delayedWrite.IsCompleted, "The first dispatched command remains simulator-owned until it settles.");
        await delayedWrite.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(bridge.Failure);
    }

    [Fact]
    public async Task ConcurrentAdmissionRejectsExcessRequestsAndReleasesSettledSlots()
    {
        const int contenders = 16;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var releaseReader = new ManualResetEventSlim();
        using var startTogether = new Barrier(contenders);
        var readerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        await using var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        bridge.SampleReceived += _ =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0)
                return;
            readerBlocked.SetResult();
            releaseReader.Wait(deadline.Token);
        };
        var start = Task.Run(() => bridge.Start(SimulatorPath()), deadline.Token);
        Task[] requests = [];
        Task<Task>[] attempts = [];
        var admittedWhileRepliesHeld = 0;
        try
        {
            await readerBlocked.Task.WaitAsync(deadline.Token);
            var reserved = Enumerable.Range(0, 7).Select(_ => bridge.InjectAsync("stale", value: 0)).ToArray();
            attempts = Enumerable.Range(0, contenders).Select(_ => Task.Factory.StartNew(() =>
            {
                startTogether.SignalAndWait(deadline.Token);
                return bridge.InjectAsync("stale", value: 0);
            }, deadline.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var raced = await Task.WhenAll(attempts).WaitAsync(deadline.Token);
            requests = [.. reserved, .. raced];
            admittedWhileRepliesHeld = requests.Count(request => !request.IsCompleted);
        }
        finally
        {
            releaseReader.Set();
            await start.WaitAsync(deadline.Token);
            await Task.WhenAll(attempts).WaitAsync(deadline.Token);
        }

        var accepted = 0;
        var rejected = 0;
        foreach (var request in requests)
        {
            try
            {
                await request.WaitAsync(deadline.Token);
                accepted++;
            }
            catch (IOException error)
            {
                Assert.Equal("simulator-request-capacity", error.Message);
                rejected++;
            }
        }

        Assert.Equal(8, admittedWhileRepliesHeld);
        Assert.Equal(8, accepted);
        Assert.Equal(contenders - 1, rejected);
        await bridge.InjectAsync("stale", value: 0).WaitAsync(deadline.Token);
        Assert.Null(bridge.Failure);
    }

    [Fact]
    public async Task DisposingWhileADelayedWriteIsDispatchingSettlesTheOwnedTaskWithCancellation()
    {
        var bridge = new SimulatorProcessBridge(NullLogger<SimulatorProcessBridge>.Instance);
        try
        {
            bridge.Start(SimulatorPath());
            await bridge.InjectAsync("delay", value: 1800);
            var pendingWrite = bridge.SetMotionAsync(12, new(0, 12, 10, 20, 50), TestContext.Current.CancellationToken);
            Assert.False(pendingWrite.IsCompleted);

            await bridge.DisposeAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingWrite);
            Assert.Null(bridge.Failure);
        }
        finally
        {
            await bridge.DisposeAsync();
        }
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(typeof(SimulatorProcessBridgeFailureTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);

    private static string SimulatorPath()
    {
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(RepositoryRoot(), "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the Rust simulator adapter with cargo build --release --locked before these integration tests.");
        return path;
    }
}
