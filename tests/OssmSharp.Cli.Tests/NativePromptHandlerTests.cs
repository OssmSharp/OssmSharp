using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

public sealed class NativePromptHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptReadsStopWhileAnIssuedSetIsStillUnsettled(bool simulatorControl)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var motionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new SimulatorHostTestDevice { MotionGate = motionGate };
        using var input = new StopAfterIssuedReader(device.MotionIssued.Task);
        using var intake = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        await device.StartAsync(intake.Token);
        await connected.Task.WaitAsync(intake.Token);

        var running = new PromptHandler(input, output, errors).RunAsync(
            device, () => null, intake.Token, simulatorControl: simulatorControl);
        try
        {
            await device.MotionIssued.Task.WaitAsync(TimeSpan.FromSeconds(2), intake.Token);
            await device.MotionRevoked.Task.WaitAsync(TimeSpan.FromSeconds(2), intake.Token);
            Assert.False(device.IsReady);
            Assert.False(running.IsCompleted);
            motionGate.TrySetResult();
            await running.WaitAsync(intake.Token);
            Assert.Equal(0, device.LastMotion?.Speed);
            Assert.Equal(3, device.MotionCount); // Initialize zero, issued speed, serialized Stop zero.
            await device.StopAsync(intake.Token);
        }
        finally
        {
            motionGate.TrySetResult();
            if (!running.IsCompleted)
                await intake.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (intake.IsCancellationRequested)
            {
                // Settle a prompt that did not reach quit.
            }
        }
    }

    [Fact]
    public async Task InitializeRequiresExplicitPromptCommandAndNeverStartsMotion()
    {
        using var input = new StringReader("status\ninitialize\nstatus\nquit\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        await device.StartAsync(TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);

        await new PromptHandler(input, output, errors).RunAsync(
            device, () => null, TestContext.Current.CancellationToken);

        Assert.Contains("Readiness: Paused", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Readiness: Ready", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
        Assert.Equal(string.Empty, errors.ToString());
        await device.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopDisablesSubsequentSetsUntilExplicitInitialize()
    {
        using var input = new StringReader(
            "initialize\nstop\nset --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\n"
            + "status\ninitialize\nstatus\nquit\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        await device.StartAsync(TestContext.Current.CancellationToken);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        if (device.ConnectionState == DeviceConnectionState.Connected)
            connected.TrySetResult();
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);

        await new PromptHandler(input, output, errors).RunAsync(
            device, () => null, TestContext.Current.CancellationToken);

        Assert.Contains("Readiness: Paused", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Readiness: Ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Motion unavailable", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, device.MotionCount); // Initialize, Stop, explicit reset.
        Assert.Equal(0, device.LastMotion?.Speed);
        await device.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NativeFeedbackStatusDoesNotTreatConnectionAsMotionReadiness()
    {
        using var input = new StringReader(
            "status\nset --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nquit\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        await device.StartAsync(TestContext.Current.CancellationToken);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        if (device.ConnectionState == DeviceConnectionState.Connected)
            connected.TrySetResult();
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);

        await new PromptHandler(input, output, errors).RunAsync(
            device, () => null, TestContext.Current.CancellationToken);

        Assert.Contains("Readiness: Paused", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Reported at", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Motion unavailable", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.MotionCount); // Only the explicit Stop writes zero.
        await device.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FeedbackTimestampIsPreservedWhenReadingLatestState()
    {
        var clock = new FixedUtcTimeProvider(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var device = new ClockedTestDevice(clock);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        await device.StartAsync(TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();

        await new PromptHandler(input, output, errors).RunAsync(
            device, () => null, TestContext.Current.CancellationToken);

        Assert.Contains("2001-01-01T00:00:00", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, device.MotionCount);
        await device.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StopAfterIssuedReader(Task issued) : TextReader
    {
        private readonly Queue<string> _lines = new(
        [
            "initialize",
            "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke",
            "stop",
            "quit",
        ]);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_lines.Count == 0)
                return null;

            var line = _lines.Dequeue();
            if (line == "stop")
                await issued.WaitAsync(cancellationToken).ConfigureAwait(false);

            return line;
        }
    }
}
