using System.Reflection;

namespace OSSMDotNet.ControlService.IntegrationTests;

/// <summary>
/// Verifies that control operations can recover and stop a native simulator device.
/// </summary>
public sealed class ControlServiceSimulatorIntegrationTests
{
    [Fact]
    public async Task TransportNeutralOperationsControlTheNativeSimulator()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var firstState = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = ResolveSimulatorPath() }),
            TimeProvider.System,
            NullLogger<SimulatedDevice>.Instance);
        device.DeviceStateChanged += change => CompleteState(change, firstState);
        await using var engine = new ControlEngine(device, TimeProvider.System);
        await using var authority = new ControlAuthority(engine, TimeProvider.System);
        using var activity = new ControlActivityProvider(TimeProvider.System);
        var operations = new ControlOperations(
            authority,
            Options.Create(new ControlServiceOptions
            {
                GuestOrigin = "http://127.0.0.1:5182",
                SimulatorPath = ResolveSimulatorPath()
            }),
            activity,
            NullLogger<ControlOperations>.Instance);

        await device.StartAsync(timeout.Token);
        await firstState.Task.WaitAsync(timeout.Token);
        authority.Tick();
        var ready = operations.Recover(operations.Read().Precondition);
        var stopped = operations.Stop();

        Assert.True(ready.Snapshot.BridgeReady);
        Assert.True(ready.Snapshot.IsSimulator);
        Assert.Equal(("Paused", "operator-stop"), (stopped.Snapshot.Mode, stopped.Snapshot.Reason));
    }

    private static void CompleteState(
        OneOf<DeviceState, Exception> change,
        TaskCompletionSource<DeviceState> completion)
    {
        if (change.TryPickT0(out var state, out _))
            completion.TrySetResult(state);
        else
            completion.TrySetException(change.AsT1);
    }

    private static string ResolveSimulatorPath()
    {
        var root = typeof(ControlServiceSimulatorIntegrationTests).Assembly
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot")
            .Value!;
        return Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release",
            OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge");
    }
}
