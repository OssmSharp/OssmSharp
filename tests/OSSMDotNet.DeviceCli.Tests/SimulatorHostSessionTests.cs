using OSSMDotNet.DeviceCli.Simulator;
using OSSMDotNet.DeviceCli.Simulator.Ipc;
using OSSMDotNet.DeviceCli.Tests.Helpers;

namespace OSSMDotNet.DeviceCli.Tests;

public sealed class SimulatorHostSessionTests
{
    [Fact]
    public async Task HostFailureEmitsConnectionLifecycleLogs()
    {
        var endpoint = new SimulatorEndpoint(
            Guid.NewGuid().ToString("N"),
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.lock"));
        var invalidSimulator = Path.GetTempFileName();
        using var output = new StringWriter();
        var originalOutput = Console.Out;

        try
        {
            Console.SetOut(output);

            await Assert.ThrowsAsync<IOException>(() => SimulatorHost.RunAsync(
                invalidSimulator, endpoint, TestContext.Current.CancellationToken));
        }
        finally
        {
            Console.SetOut(originalOutput);
            File.Delete(invalidSimulator);
        }

        Assert.Contains("Device connection changed from Disconnected to Connecting.", output.ToString());
        Assert.Contains("Device connection failed.", output.ToString());
    }

    [Theory]
    [InlineData(0, 20, 50, 100)]
    [InlineData(101, 20, 50, 100)]
    [InlineData(12, 0, 50, 100)]
    [InlineData(12, 101, 101, 100)]
    [InlineData(12, 1, 0, 100)]
    [InlineData(12, 20, 101, 100)]
    [InlineData(12, 51, 50, 100)]
    [InlineData(12, 20, 50, 99)]
    [InlineData(12, 20, 50, 60_001)]
    public async Task MoveOutsideBoundsIsRejectedWithoutDispatchingMotion(
        int speed, int stroke, int depth, int durationMilliseconds)
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = new SimulatorEndpoint(Guid.NewGuid().ToString("N")[..12], string.Empty);
        await using var session = new SimulatorHostSession("unused", endpoint, device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);

        try
        {
            var response = await SendWhenReadyAsync(
                endpoint, new SimulatorIpcRequest("move", Speed: speed, Stroke: stroke, Depth: depth, DurationMilliseconds: durationMilliseconds), timeout.Token);

            Assert.False(response.Ok);
            Assert.Equal("The motion request is outside the supported bounds.", response.Error);
            Assert.Equal(0, device.MotionCount);
        }
        finally
        {
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Theory]
    [InlineData(1, 1, 1, 100)]
    [InlineData(100, 100, 100, 60_000)]
    public async Task MoveAtBoundsDispatchesRequestedMotion(
        int speed, int stroke, int depth, int durationMilliseconds)
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = new SimulatorEndpoint(Guid.NewGuid().ToString("N")[..12], string.Empty);
        await using var session = new SimulatorHostSession("unused", endpoint, device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);

        try
        {
            var response = await SendWhenReadyAsync(
                endpoint, new SimulatorIpcRequest("move", Speed: speed, Stroke: stroke, Depth: depth, DurationMilliseconds: durationMilliseconds), timeout.Token);

            Assert.True(response.Ok, response.Error);
            Assert.Equal(speed, response.Output!.State.MotionSettings.Speed);
            Assert.Equal(stroke, response.Output.State.MotionSettings.Stroke);
            Assert.Equal(depth, response.Output.State.MotionSettings.Depth);
            Assert.Equal(1, device.MotionCount);
        }
        finally
        {
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task UnknownOperationIsRejectedWithoutChangingMotion()
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = new SimulatorEndpoint(Guid.NewGuid().ToString("N")[..12], string.Empty);
        await using var session = new SimulatorHostSession("unused", endpoint, device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);

        try
        {
            var response = await SendWhenReadyAsync(endpoint, new SimulatorIpcRequest("unknown"), timeout.Token);

            Assert.False(response.Ok);
            Assert.Equal("Unknown simulator host operation.", response.Error);
            Assert.Equal(0, device.MotionCount);
        }
        finally
        {
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task StartWithMatchingPathReportsStateWithoutChangingMotion()
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = new SimulatorEndpoint(Guid.NewGuid().ToString("N")[..12], string.Empty);
        await using var session = new SimulatorHostSession("unused", endpoint, device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);

        try
        {
            var start = await SendWhenReadyAsync(endpoint, new SimulatorIpcRequest("start", "unused"), timeout.Token);
            var status = await SimulatorClient.SendAsync(endpoint, new SimulatorIpcRequest("status"), timeout.Token);

            Assert.True(start.Ok, start.Error);
            Assert.Equal(0, start.Output!.State.MotionSettings.Speed);
            Assert.True(status.Ok, status.Error);
            Assert.Equal(start.Output.State.SessionId, status.Output!.State.SessionId);
            Assert.Equal(0, device.MotionCount);
        }
        finally
        {
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task UnexpectedDisconnectEndsSessionWithFailure()
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = new SimulatorEndpoint(Guid.NewGuid().ToString("N")[..12], string.Empty);
        await using var session = new SimulatorHostSession("unused", endpoint, device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);

        try
        {
            var status = await SendWhenReadyAsync(endpoint, new SimulatorIpcRequest("status"), timeout.Token);
            Assert.True(status.Ok, status.Error);

            device.Disconnect();

            var failure = await Assert.ThrowsAsync<IOException>(() => run);
            Assert.Equal("The simulator failed while running the diagnostic host.", failure.Message);
            Assert.Equal("The simulator stopped unexpectedly.", failure.InnerException?.Message);
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    [Fact]
    public async Task StopAfterMoveSettlesSpeedAndEndsSession()
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = new SimulatorEndpoint(Guid.NewGuid().ToString("N")[..12], string.Empty);
        await using var session = new SimulatorHostSession("unused", endpoint, device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);

        try
        {
            var move = await SendWhenReadyAsync(
                endpoint, new SimulatorIpcRequest("move", Speed: 12, Stroke: 20, Depth: 50, DurationMilliseconds: 60_000), timeout.Token);
            var stop = await SimulatorClient.SendAsync(endpoint, new SimulatorIpcRequest("stop"), timeout.Token);

            Assert.True(move.Ok, move.Error);
            Assert.Equal(12, move.Output!.State.MotionSettings.Speed);
            Assert.True(stop.Ok, stop.Error);
            Assert.Equal(0, stop.Output!.State.MotionSettings.Speed);
            Assert.True(device.MotionCount >= 2);
            Assert.Equal(0, device.LastMotion!.Speed);
            Assert.Equal(0, await run);
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    [Fact]
    public async Task DisposingSessionDoesNotDisposeInjectedDevice()
    {
        await using var device = new SimulatorHostTestDevice();
        var endpoint = SimulatorEndpoint.CreateUnique();

        await using (var session = new SimulatorHostSession("unused", endpoint, device))
        {
        }

        Assert.False(device.Disposed);
    }

    private static async Task<SimulatorIpcResponse> SendWhenReadyAsync(
        SimulatorEndpoint endpoint, SimulatorIpcRequest request, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await SimulatorClient.SendAsync(endpoint, request, cancellationToken);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(25, cancellationToken);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
