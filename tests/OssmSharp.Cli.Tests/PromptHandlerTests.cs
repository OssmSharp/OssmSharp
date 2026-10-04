using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PromptHandlerTests : MockerTestBase<object>, IAsyncDisposable
{
    private readonly PromptInput input = new();
    private StringWriter output = null!;
    private StringWriter error = null!;
    private Func<string?, bool>? outputFailure;
    private Func<string?, bool>? errorFailure;

    protected override Func<Mocker, object> CreateComponentAction => mocks => new PromptHandler(
        mocks.GetOrCreateMock<IOssmDevice>().Instance,
        input,
        output,
        error,
        new PromptConsole(output, error));

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        output = new FailingWriter(line => outputFailure?.Invoke(line) == true);
        error = new FailingWriter(line => errorFailure?.Invoke(line) == true);
        var device = Mock.Get(mocks.GetOrCreateMock<IOssmDevice>().Instance);
        device.Setup(value => value.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetResult(SetStatus.Completed, 5));
        device.Setup(value => value.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(value => value.ResetAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(value => value.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(0, 0, 0, 0, 0));
    };

    private new PromptHandler Component => (PromptHandler)base.Component;

    private Mock<IOssmDevice> Device => Mock.Get(Mocks.GetOrCreateMock<IOssmDevice>().Instance);

    public ValueTask DisposeAsync()
    {
        try
        {
            output.Dispose();
            error.Dispose();
        }
        finally
        {
            base.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CancellationAwaitsIssuedSettings()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationToken = CancellationToken.None;
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Returns((MotionSettings _, CancellationToken token) =>
            {
                operationToken = token;
                entered.TrySetResult();
                return release.Task;
            });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = Component.RunAsync(cancellation.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.SendLine("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();

            operationToken.IsCancellationRequested.Should().BeTrue();
            run.IsCompleted.Should().BeFalse();
            output.ToString().Should().NotContain("CANCELLED set");
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult(new SetResult(SetStatus.Cancelled, 1));
            await run;
        }

        (await run).Should().Be(0);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(new MotionSettings(0, 20, 50, 50, 25), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        output.ToString().Should().Contain("CANCELLED set: 1 commands sent");
    }

    [Fact]
    public async Task StopAwaitsDeviceEvenWhenProgressOutputFails()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        errorFailure = line => line?.StartsWith("Sending stop", StringComparison.Ordinal) == true;
        input.SendLine("stop");
        input.SendLine("quit");
        var run = Component.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await run;
        }

        (await run).Should().Be(1);
        output.ToString().Should().NotContain("OK stop");
    }

    [Fact]
    public async Task FailedResultOutputNeverClaimsNothingWasSent()
    {
        outputFailure = line => line?.StartsWith("OK set", StringComparison.Ordinal) == true;
        input.SendLine("reset");
        input.SendLine("set 0 20 50 50 25");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Mocks.Verify<IOssmDevice>(device => device.SetAsync(new MotionSettings(0, 20, 50, 50, 25), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        error.ToString().Should().Contain("ERROR set:").And.NotContain("Nothing sent");
    }

    [Fact]
    public async Task FailedErrorOutputCannotHideTerminalFailure()
    {
        Device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("read failed"));
        errorFailure = _ => true;
        input.SendLine("status");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(true, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidOperationDuringLiveReadIsTerminalNotInputRejection()
    {
        Device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("native read failed"));
        input.SendLine("status");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        error.ToString().Should().Contain("native read failed").And.NotContain("Nothing sent");
    }

    [Theory]
    [InlineData("set 0 120 50 50 25", "speed '120'")]
    [InlineData("set 0 50 -1 50 25", "stroke '-1'")]
    [InlineData("set 0 50 50 NaN 25", "depth 'NaN'")]
    [InlineData("set 0 50 50 50 Infinity", "sensation 'Infinity'")]
    [InlineData("set 0 5.5 5 5 5", "speed '5.5'")]
    [InlineData("set 0 5 5.0 5 5", "stroke '5.0'")]
    [InlineData("set 0 5 5 5.5 5", "depth '5.5'")]
    [InlineData("set 0 5 5 5 5.5", "sensation '5.5'")]
    [InlineData("set 0 5e1 5 5 5", "speed '5e1'")]
    [InlineData("set -1 20 50 50 25", "pattern '-1'")]
    [InlineData("set 1.5 20 50 50 25", "pattern '1.5'")]
    [InlineData("set 2147483648 20 50 50 25", "pattern '2147483648'")]
    [InlineData("set 7 20 50 50 25", "pattern '7'")]
    [InlineData("set 01 20 50 50 25", "pattern '01'")]
    [InlineData("set Deeper,Insist 20 50 50 25", "pattern 'Deeper,Insist'")]
    [InlineData("set 0 20 50 50 25 extra", "extra")]
    [InlineData("status --fresh", "--fresh")]
    public async Task InvalidInputSendsNothingAndSessionContinues(string command, string offending)
    {
        input.SendLine("reset");
        input.SendLine(command);
        input.SendLine("status");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(true, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        error.ToString().Should().Contain("ERROR input:").And.Contain(offending).And.NotContain("Nothing sent").And.NotContain("Armed:");
        output.ToString().Should().Contain("OK status:").And.NotContain("OK set");
    }

    [Theory]
    [InlineData("set", "pattern, speed, stroke, depth, sensation")]
    [InlineData("set simplestroke", "speed, stroke, depth, sensation")]
    [InlineData("set 0 20 50", "depth, sensation")]
    public async Task MissingSetArgumentsPrintSingleUsageError(string command, string missing)
    {
        input.SendLine(command);
        input.SendLine("help set");
        input.SendLine("status");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        const string usage = "set <pattern> <speed> <stroke> <depth> <sensation>";
        error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("ERROR", StringComparison.Ordinal))
            .Should().Equal($"ERROR set: Missing {missing}; expected {usage}.");
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        output.ToString().Should().Contain(usage).And.Contain("OK status:").And.NotContain("OK set");
    }

    [Fact]
    public async Task SettingsNeedNoImplicitReset()
    {
        input.SendLine("set 0 20 50 50 25");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        Mocks.Verify<IOssmDevice>(device => device.SetAsync(new MotionSettings(0, 20, 50, 50, 25), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        output.ToString().Should().Contain("OK set").And.NotContain("Armed:");
        error.ToString().Should().NotContain("ERROR");
    }

    [Fact]
    public async Task QuitSendsStopAndWaitsForItBeforeExiting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        input.SendLine("quit");
        input.SendLine("set 0 20 50 50 25");
        var run = Component.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            run.IsCompleted.Should().BeFalse();
            error.ToString().Should().Contain("Sending stop command...");
            output.ToString().Should().NotContain("OK stop");
        }
        finally
        {
            release.TrySetResult();
            await run;
        }

        (await run).Should().Be(0);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        output.ToString().Should().Contain("OK stop");
    }

    [Fact]
    public async Task CommandsUsePublicDeviceOperationsAndLiveState()
    {
        var operations = new List<string>();
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("stop")).Returns(Task.CompletedTask);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("set")).ReturnsAsync(new SetResult(SetStatus.Completed, 5));
        Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("reset")).Returns(Task.CompletedTask);
        Device.Setup(device => device.GetStateAsync(true, It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("status")).ReturnsAsync(new DeviceState(0, 0, 0, 0, 0));
        foreach (var line in new[] { "stop", "set 0 20 50 50 25", "reset", "set insist 100 0 50 25", "status", "help", "help set", "help bogus", "stop", "quit" })
        {
            input.SendLine(line);
        }

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        operations.Should().Equal("stop", "set", "reset", "set", "status", "stop", "stop");
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(new MotionSettings(0, 20, 50, 50, 25), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(new MotionSettings(StrokePattern.Insist, 100, 0, 50, 25), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        error.ToString().Should().NotContain("Nothing sent").And.Contain("'bogus'").And.NotContain("Armed:");
        output.ToString().Should().Contain("OK reset: ready for settings.")
            .And.Contain("OK set: settings sent.")
            .And.Contain("OK status: pattern=0 speed=Unknown")
            .And.NotContain("cached")
            .And.Contain("set <pattern> <speed> <stroke> <depth> <sensation>")
            .And.Contain("Speed (as an integer percentage)")
            .And.Contain("0=SimpleStroke, 1=TeasingPounding, 2=RoboStroke, 3=HalfNHalf, 4=Deeper, 5=StopNGo, 6=Insist")
            .And.Contain("OK stop").And.NotContain("Armed:");
    }

    [Fact]
    public async Task IdleCancellationStopsAndExits()
    {
        var run = Component.RunAsync(TestContext.Current.CancellationToken);
        input.Send(new("partial command", IsInterrupted: true));
        input.SendLine("set 0 20 50 50 25");

        (await run).Should().Be(0);

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        output.ToString().Should().Contain("OK stop").And.NotContain("OK set");
    }

    [Fact]
    public async Task ExecutionCancellationStartsStopBeforeIssuedSetSettles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationToken = CancellationToken.None;
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Returns((MotionSettings _, CancellationToken token) =>
            {
                operationToken = token;
                entered.TrySetResult();
                return release.Task;
            });
        Device.Setup(device => device.EmergencyStopAsync(CancellationToken.None)).Returns(() =>
        {
            stopSent.TrySetResult();
            return Task.CompletedTask;
        });
        using var session = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var command = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = Component.RunAsync(session.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.Send(new("set 0 20 50 50 25", CommandCancellationToken: command.Token));
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await command.CancelAsync();
            await stopSent.Task.WaitAsync(TestContext.Current.CancellationToken);
            operationToken.IsCancellationRequested.Should().BeTrue();
            run.IsCompleted.Should().BeFalse();
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        }
        finally
        {
            release.TrySetResult(new SetResult(SetStatus.Cancelled, 1));
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            await session.CancelAsync();
            await run;
        }

        (await run).Should().Be(0);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        output.ToString().Should().Contain("CANCELLED set: 1 commands sent").And.Contain("OK stop");
        error.ToString().Should().Contain("Sending stop command...");
    }

    [Fact]
    public async Task CompletedCommandCancellationDoesNotStopDuringNextRead()
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var command = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = Component.RunAsync(session.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.Send(new("status", CommandCancellationToken: command.Token));
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            await command.CancelAsync();

            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            output.ToString().Should().Contain("OK status").And.NotContain("OK stop");
        }
        finally
        {
            await session.CancelAsync();
            await run;
        }

        (await run).Should().Be(0);
    }

    [Fact]
    public async Task FailedStopExitsWithErrorWithoutClaimingCompletion()
    {
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Stop failed."));
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        error.ToString().Should().Contain("ERROR stop: Stop failed.").And.NotContain("No rollback claimed");
        output.ToString().Should().NotContain("OK stop");
    }

    [Fact]
    public async Task CommandsExecuteSequentially()
    {
        var first = new MotionSettings(0, 20, 50, 50, 25);
        var second = new MotionSettings(StrokePattern.TeasingPounding, 30, 60, 40, 35);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.SetAsync(first, It.IsAny<CancellationToken>())).Returns(() =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        Device.Setup(device => device.SetAsync(second, It.IsAny<CancellationToken>())).Returns(() =>
        {
            completed.TrySetResult();
            return Task.FromResult(new SetResult(SetStatus.Completed, 5));
        });
        var run = Component.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.SendLine("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            input.SendLine("set 1 30 60 40 35");

            Mocks.Verify<IOssmDevice>(device => device.SetAsync(second, FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            output.ToString().Should().NotContain("OK set");
            release.TrySetResult(new SetResult(SetStatus.Completed, 5));
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult(new SetResult(SetStatus.Completed, 5));
            input.SendLine("quit");
            await run;
        }

        (await run).Should().Be(0);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(first, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(second, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        output.ToString().Split(Environment.NewLine).Count(line => line == "OK set: settings sent.").Should().Be(2);
        output.ToString().Should().NotContain("SUPERSEDED");
    }

    [Fact]
    public async Task IdleConnectionFailureCancelsInputAndEndsSession()
    {
        var run = Component.RunAsync(TestContext.Current.CancellationToken);
        await input.NextReadAsync(TestContext.Current.CancellationToken);
        Device.SetupGet(device => device.ConnectionFailure).Returns(new IOException("Disconnected"));
        Device.Raise(device => device.StateChanged += null, EventArgs.Empty);

        (await run.WaitAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Device.VerifyAdd(device => device.StateChanged += It.IsAny<EventHandler>(), Times.Once);
        Device.VerifyRemove(device => device.StateChanged -= It.IsAny<EventHandler>(), Times.Once);
        error.ToString().Should().Contain("reconnect");
    }

    [Fact]
    public async Task WriteFailureShowsPartialProgressAndEndsSession()
    {
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DeviceWriteException(2, new IOException("write failed")));
        input.SendLine("reset");
        input.SendLine("set 0 20 50 50 25");
        input.SendLine("reset");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(new MotionSettings(0, 20, 50, 50, 25), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        error.ToString().Should().Contain("ERROR set: Could not send settings after 2 completed writes. Settings may be partially applied.");
        output.ToString().Should().NotContain("OK set");
    }

    [Fact]
    public async Task FailedLiveReadDoesNotFallBackToCachedSuccess()
    {
        Device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("read failed"));
        input.SendLine("status");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(true, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(false, FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        output.ToString().Should().NotContain("OK status");
        error.ToString().Should().Contain("ERROR status").And.Contain("read failed");
    }

    [Theory]
    [InlineData(1.5, 2, "75%")]
    [InlineData(2.5, 20, "12.5%")]
    [InlineData(40d, 10, "400%")]
    [InlineData(0d, 10, "0%")]
    [InlineData(5d, 0, "Unknown")]
    [InlineData(0d, 0, "Unknown")]
    public async Task StatusAlwaysReadsLiveState(double currentDeviceVelocityPercent, int stroke, string expectedSpeed)
    {
        Device.Setup(device => device.GetStateAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(StrokePattern.Deeper, 5, 6, 7, 8));
        Device.Setup(device => device.GetStateAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(StrokePattern.Insist, currentDeviceVelocityPercent, stroke, 3, 4));
        input.SendLine("status");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(true, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(false, FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        output.ToString().Should().Contain($"OK status: pattern=6 speed={expectedSpeed} stroke={stroke} depth=3 sensation=4.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResultColoursHighlightOnlyOutcomeKeywords(bool colour)
    {
        var console = new PromptConsole(output, error, colourOutput: colour, colourError: colour);
        base.Component = new PromptHandler(Device.Object, input, output, error, console);
        input.SendLine("reset");
        input.SendLine("set 0 1.5 5 5 5");
        input.SendLine("quit");

        (await Component.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        output.ToString().Should().Contain(colour ? "\u001b[32mOK\u001b[0m reset:" : "OK reset:");
        error.ToString().Should().Contain(colour ? "\u001b[31mERROR\u001b[0m input:" : "ERROR input:");
        if (!colour)
        {
            output.ToString().Should().NotContain("\u001b");
            error.ToString().Should().NotContain("\u001b");
        }

        error.ToString().Should().NotContain("ossm> ");
    }
}
