using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

public sealed class SimulatorPromptTests
{
    [Fact]
    public async Task SimulatorSetAndStopSettleAtZeroBeforeReleasingOwnership()
    {
        using var input = new StringReader(
            "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        await using var device = new SimulatorHostTestDevice();
        var stoppedChild = false;

        var handler = new PromptHandler(input, output, error, () => lease);
        var exitCode = await handler.RunWithOwnershipAsync(
            () => device,
            _ => Task.CompletedTask,
            () =>
            {
                Assert.True(lease.CanRead);
                Assert.Equal(0, device.LastMotion?.Speed);
                stoppedChild = true;
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken, simulatorControl: true);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("write settled", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(device.MotionCount >= 3);
        Assert.True(stoppedChild);
        Assert.False(lease.CanRead);
    }

    [Fact]
    public async Task MovingEnvelopeChangeRequiresStopBeforeAnotherWrite()
    {
        using var input = new StringReader(
            "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\n" +
            "set --speed 12 --stroke 30 --depth 60 --pattern SimpleStroke\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        await using var device = new SimulatorHostTestDevice();

        var handler = new PromptHandler(input, output, error, () => lease);
        await handler.RunWithOwnershipAsync(
            () => device,
            _ => Task.CompletedTask, () => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken, simulatorControl: true);

        Assert.Contains("Stop and confirm zero", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
        Assert.False(lease.CanRead);
    }

    [Fact]
    public async Task InvalidMotionInputDoesNotDispatchMovingSettings()
    {
        using var input = new StringReader(
            "set --speed 20 --stroke 0 --depth 50 --pattern SimpleStroke\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        await using var device = new SimulatorHostTestDevice();

        var handler = new PromptHandler(input, output, error, () => lease);
        await handler.RunWithOwnershipAsync(
            () => device,
            _ => Task.CompletedTask, () => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken, simulatorControl: true);

        Assert.Contains("positive stroke", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(100, 100, 100)]
    public async Task BoundaryMotionSettingsAreSentBeforeExitStop(int speed, int stroke, int depth)
    {
        using var input = new StringReader(
            $"set --speed {speed} --stroke {stroke} --depth {depth} --pattern SimpleStroke\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        await using var device = new SimulatorHostTestDevice();
        var reported = new List<MotionSettings>();
        device.DeviceStateChanged += change =>
        {
            if (change.IsT0)
                reported.Add(change.AsT0.MotionSettings);
        };

        var handler = new PromptHandler(input, output, error, () => lease);
        await handler.RunWithOwnershipAsync(
            () => device,
            _ => Task.CompletedTask, () => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken, simulatorControl: true);

        Assert.Contains(reported, settings =>
            settings.Speed == speed && settings.Stroke == stroke && settings.Depth == depth);
        Assert.Equal(0, device.LastMotion?.Speed);
        Assert.False(lease.CanRead);
    }

    [Fact]
    public async Task BusyOwnerPreventsSimulatorStartup()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var started = false;

        var handler = new PromptHandler(input, output, error, () => null);
        var exitCode = await handler.RunWithOwnershipAsync(
            () => throw new InvalidOperationException("Device started without ownership."),
            _ =>
            {
                started = true;
                return Task.CompletedTask;
            },
            () => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken, simulatorControl: true);

        Assert.Equal(1, exitCode);
        Assert.False(started);
        Assert.Contains("owned", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupFailureStopsChildBeforeReleasingOwnership()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        var stoppedChild = false;

        var handler = new PromptHandler(input, output, error, () => lease);
        var exitCode = await handler.RunWithOwnershipAsync(
            () => throw new InvalidOperationException("Device created after startup failure."),
            _ => throw new IOException("Simulator startup failed."),
            () =>
            {
                Assert.True(lease.CanRead);
                stoppedChild = true;
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken, simulatorControl: true);

        Assert.Equal(1, exitCode);
        Assert.True(stoppedChild);
        Assert.False(lease.CanRead);
        Assert.Contains("Simulator startup failed", error.ToString(), StringComparison.Ordinal);
    }
}
