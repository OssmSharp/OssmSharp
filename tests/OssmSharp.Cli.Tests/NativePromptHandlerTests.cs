using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Runtime;
using DeviceConnectionState = OssmSharp.Device.Primitives.Models.DeviceConnectionState;

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
        await using var execution = new DeviceExecutionSession(device, TimeProvider.System);
        using var intake = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stoppedReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DeviceStateChanged += update =>
        {
            if (update.IsT0 && update.AsT0.MotionSettings.Speed == 0)
                stoppedReport.TrySetResult();
        };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        await device.StartAsync(TestContext.Current.CancellationToken);
        if (device.ConnectionState == DeviceConnectionState.Connected)
            connected.TrySetResult();
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stoppedReport.Task.WaitAsync(TestContext.Current.CancellationToken);

        var running = new PromptHandler(input, output, errors).RunAsync(
            device, () => execution.Reported, () => execution.Fault,
            intake.Token, simulatorControl: simulatorControl, execution: execution);
        try
        {
            await device.MotionIssued.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await device.MotionRevoked.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(DeviceExecutionState.Paused, execution.State);
            Assert.False(running.IsCompleted);
            motionGate.TrySetResult();
            await running.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, device.LastMotion?.Speed);
            Assert.Equal(3, device.MotionCount); // Initialize zero, issued speed, serialized Stop zero.
            await device.StopAsync(TestContext.Current.CancellationToken);
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
                // The test canceled input to settle a prompt that did not reach quit.
            }
        }
    }

    [Fact]
    public async Task InitializeRequiresExplicitPromptCommandAndSettlesZero()
    {
        using var input = new StringReader("status\ninitialize\nstatus\nquit\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        await using var execution = new DeviceExecutionSession(device, TimeProvider.System);
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
            device, () => execution.Reported, () => execution.Fault,
            TestContext.Current.CancellationToken, simulatorControl: false, execution: execution);

        Assert.Contains("Readiness: Paused", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Readiness: Ready", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
        Assert.Equal(string.Empty, errors.ToString());
        await device.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NativeFeedbackStatusDoesNotTreatConnectionAsMotionReadiness()
    {
        using var input = new StringReader(
            "status\nset --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\n"
            + "acknowledge\nstop\nquit\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        await using var execution = new DeviceExecutionSession(device, TimeProvider.System);
        await device.StartAsync(TestContext.Current.CancellationToken);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (device.ConnectionState == DeviceConnectionState.Connected)
            connected.TrySetResult();
        else
            device.ConnectionStateChanged += state =>
            {
                if (state == DeviceConnectionState.Connected)
                    connected.TrySetResult();
            };
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);
        var prompt = new PromptHandler(input, output, errors);

        await prompt.RunAsync(
            device, () => execution.Reported, () => execution.Fault,
            TestContext.Current.CancellationToken, simulatorControl: false, execution: execution);

        Assert.Contains("Readiness: Paused", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Reported at", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Motion unavailable", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("Acknowledgement unavailable", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
        await device.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PromptUsesRuntimeClockForFeedbackFreshness()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var clock = new FixedUtcTimeProvider(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var device = new ClockedTestDevice(clock);
        await using var execution = new DeviceExecutionSession(device, clock);
        var submissionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new SubmissionGatedPromptReader(
            [
                "initialize",
                "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke",
                "quit",
            ],
            2,
            submissionCompleted.Task);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };

        await device.StartAsync(timeout.Token);
        await connected.Task.WaitAsync(timeout.Token);
        try
        {
            await new PromptHandler(input, output, errors).RunAsync(
                device,
                () => execution.Reported,
                () => execution.Fault,
                timeout.Token,
                recordSubmission: _ => submissionCompleted.TrySetResult(),
                execution: execution);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail($"Prompt did not complete: {errors}");
        }

        Assert.Equal(2, device.MotionCount);
        Assert.DoesNotContain("recent stopped-ready feedback is required", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Motion unavailable", errors.ToString(), StringComparison.Ordinal);
        await execution.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, device.MotionCount);
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
