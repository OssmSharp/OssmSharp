using AwesomeAssertions;
using OssmSharp.Runtime.Oem;
using OssmSharp.Runtime.Oem.Tests;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PromptHandlerTests
{
    [Fact]
    public async Task CancellationAwaitsIssuedSettings()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Write = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            },
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = new PromptHandler(device, input, output, error).RunAsync(cancellation.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.SendLine("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();

            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            await run;
        }

        (await run).Should().Be(0);
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0));
        output.ToString().Should().Contain("CANCELLED set: 1 commands sent");
    }

    [Fact]
    public async Task StopAwaitsItsWriteEvenWhenProgressOutputFails()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice { Write = (_, _) => release.Task };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("stop", "quit");
        using var output = new StringWriter();
        using var error = new FailingWriter(line => line?.StartsWith("Sending stop", StringComparison.Ordinal) == true);
        var console = new PromptConsole(output, error);
        var run = new PromptHandler(device, input, output, error, console).RunAsync(TestContext.Current.CancellationToken);
        try
        {
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await run;
        }

        (await run).Should().Be(1);
    }

    [Fact]
    public async Task FailedResultOutputNeverClaimsNothingWasSent()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("reset", "set 0 20 50 50 25", "quit");
        using var output = new FailingWriter(line => line?.StartsWith("OK set", StringComparison.Ordinal) == true);
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        device.Writes.Should().HaveCount(5);
        error.ToString().Should().NotContain("Nothing sent");
    }

    [Fact]
    public async Task FailedErrorOutputCannotHideTerminalFailure()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        device.Read = _ => Task.FromException<DeviceState>(new IOException("read failed"));
        var input = new PromptInput("status", "quit");
        using var output = new StringWriter();
        using var error = new FailingWriter(_ => true);

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidOperationDuringLiveReadIsTerminalNotInputRejection()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        device.Read = _ => Task.FromException<DeviceState>(new InvalidOperationException("native read failed"));
        var input = new PromptInput("status", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
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
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("reset", command, "status", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new PromptHandler(device, input, output, error);

        (await handler.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        error.ToString().Should().Contain("ERROR input:").And.Contain(offending).And.NotContain("Nothing sent").And.NotContain("Armed:");
        output.ToString().Should().Contain("OK status:").And.NotContain("OK set");
    }

    [Theory]
    [InlineData("set", "pattern, speed, stroke, depth, sensation")]
    [InlineData("set simplestroke", "speed, stroke, depth, sensation")]
    [InlineData("set 0 20 50", "depth, sensation")]
    public async Task MissingSetArgumentsPrintSingleUsageError(string command, string missing)
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput(command, "help set", "status", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        const string usage = "set <pattern> <speed> <stroke> <depth> <sensation>";
        error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("ERROR", StringComparison.Ordinal))
            .Should().Equal($"ERROR set: Missing {missing}; expected {usage}.");
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain(usage).And.Contain("OK status:").And.NotContain("OK set");
    }

    [Fact]
    public async Task SettingsAfterConnectionNeedNoReset()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("set 0 20 50 50 25", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Writes.Should().HaveCount(6).And.EndWith(new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("OK set").And.NotContain("Armed:");
        error.ToString().Should().NotContain("ERROR");
    }

    [Fact]
    public async Task QuitSendsStopAndWaitsForItBeforeExiting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Write = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            },
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("quit", "set 0 20 50 50 25");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);

        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            run.IsCompleted.Should().BeFalse();
            error.ToString().Should().Contain("Sending stop command...");
        }
        finally
        {
            release.TrySetResult();
        }

        (await run).Should().Be(0);
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        device.IsArmed.Should().BeTrue();
        output.ToString().Should().Contain("OK stop");
    }

    [Fact]
    public async Task CommandsPreserveRuntimeOrderingAndStateOrigin()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput(
            "stop",
            "set 0 20 50 50 25",
            "reset",
            "set insist 100 0 50 25",
            "status",
            "help",
            "help set",
            "help bogus",
            "stop",
            "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Writes.Should().Equal(
            new DeviceCommand(DeviceControl.Speed, 0),
            new DeviceCommand(DeviceControl.Pattern, 0),
            new DeviceCommand(DeviceControl.Stroke, 50),
            new DeviceCommand(DeviceControl.Depth, 50),
            new DeviceCommand(DeviceControl.Sensation, 25),
            new DeviceCommand(DeviceControl.Speed, 20),
            new DeviceCommand(DeviceControl.Pattern, 6),
            new DeviceCommand(DeviceControl.Stroke, 0),
            new DeviceCommand(DeviceControl.Depth, 50),
            new DeviceCommand(DeviceControl.Sensation, 25),
            new DeviceCommand(DeviceControl.Speed, 100),
            new DeviceCommand(DeviceControl.Speed, 0),
            new DeviceCommand(DeviceControl.Speed, 0));
        device.Reads.Should().Be(2);
        device.IsArmed.Should().BeTrue();
        error.ToString().Should().NotContain("Nothing sent")
            .And.Contain("'bogus'").And.NotContain("Armed:");
        output.ToString().Should().Contain("OK reset: ready for settings.")
            .And.Contain("OK set: settings sent.")
            .And.Contain("OK status: pattern=0 speed=0")
            .And.NotContain("cached")
            .And.Contain("set <pattern> <speed> <stroke> <depth> <sensation>")
            .And.Contain("0=SimpleStroke, 1=TeasingPounding, 2=RoboStroke, 3=HalfNHalf, 4=Deeper, 5=StopNGo, 6=Insist")
            .And.Contain("OK stop").And.NotContain("Armed:");
    }

    [Fact]
    public async Task IdleCancellationStopsAndExits()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);

        input.Send(new("partial command", IsInterrupted: true));
        input.Send(new("set 0 20 50 50 25"));

        (await run).Should().Be(0);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("OK stop").And.NotContain("OK set");
    }

    [Fact]
    public async Task ExecutionCancellationStartsStopBeforeIssuedSetSettles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Write = (command, token) =>
            {
                if (command.Control == DeviceControl.Pattern)
                {
                    entered.TrySetResult();
                    token.CanBeCanceled.Should().BeTrue();
                    return release.Task;
                }

                token.CanBeCanceled.Should().BeFalse();
                stopSent.TrySetResult();
                return Task.CompletedTask;
            },
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var command = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = new PromptHandler(device, input, output, error).RunAsync(session.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.Send(new("set 0 20 50 50 25", CommandCancellationToken: command.Token));
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await command.CancelAsync();
            await stopSent.Task.WaitAsync(TestContext.Current.CancellationToken);
            device.IsArmed.Should().BeTrue();
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0));
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            await session.CancelAsync();
        }

        (await run).Should().Be(0);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("CANCELLED set: 1 commands sent").And.Contain("OK stop");
        error.ToString().Should().Contain("Sending stop command...");
    }

    [Fact]
    public async Task CompletedCommandCancellationDoesNotStopDuringNextRead()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var command = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = new PromptHandler(device, input, output, error).RunAsync(session.Token);

        await input.NextReadAsync(TestContext.Current.CancellationToken);
        input.Send(new("status", CommandCancellationToken: command.Token));
        await input.NextReadAsync(TestContext.Current.CancellationToken);
        await command.CancelAsync();

        device.Writes.Should().BeEmpty();
        output.ToString().Should().Contain("OK status").And.NotContain("OK stop");
        await session.CancelAsync();
        (await run).Should().Be(0);
    }

    [Fact]
    public async Task FailedStopExitsWithErrorWithoutClaimingCompletion()
    {
        var failure = new IOException("Stop failed.");
        await using var device = new TestDevice();
        device.Write = (_, _) => device.Writes.Count == 1 ? Task.FromException(failure) : Task.CompletedTask;
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        error.ToString().Should().Contain("ERROR stop: Stop failed.").And.NotContain("No rollback claimed");
        output.ToString().Should().NotContain("OK stop");
    }

    [Fact]
    public async Task CommandsExecuteSequentially()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Write = (command, _) =>
            {
                if (command == new DeviceCommand(DeviceControl.Pattern, 0))
                {
                    entered.TrySetResult();
                    return release.Task;
                }

                if (command == new DeviceCommand(DeviceControl.Speed, 30))
                {
                    completed.TrySetResult();
                }

                return Task.CompletedTask;
            },
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            input.SendLine("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            input.SendLine("set 1 30 60 40 35");
            var nextRead = input.NextReadAsync(TestContext.Current.CancellationToken);
            await Task.Yield();

            nextRead.IsCompleted.Should().BeFalse();
            device.Writes.Should().ContainSingle();
            release.TrySetResult();
            await nextRead;
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
            input.SendLine("quit");
            await run;
        }

        device.Writes.Should().Equal(
            new DeviceCommand(DeviceControl.Pattern, 0),
            new DeviceCommand(DeviceControl.Stroke, 50),
            new DeviceCommand(DeviceControl.Depth, 50),
            new DeviceCommand(DeviceControl.Sensation, 25),
            new DeviceCommand(DeviceControl.Speed, 20),
            new DeviceCommand(DeviceControl.Pattern, 1),
            new DeviceCommand(DeviceControl.Stroke, 60),
            new DeviceCommand(DeviceControl.Depth, 40),
            new DeviceCommand(DeviceControl.Sensation, 35),
            new DeviceCommand(DeviceControl.Speed, 30),
            new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("OK set: settings sent.").And.NotContain("SUPERSEDED");
    }

    [Fact]
    public async Task IdleConnectionFailureCancelsInputAndEndsSession()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);
        await input.NextReadAsync(TestContext.Current.CancellationToken);
        var failure = new IOException("Disconnected");
        device.Fail(failure);

        (await run.WaitAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        device.ConnectionFailure.Should().BeSameAs(failure);
        device.Writes.Should().BeEmpty();
        error.ToString().Should().Contain("reconnect");
    }

    [Fact]
    public async Task WriteFailureShowsPartialProgressAndEndsSession()
    {
        await using var device = new TestDevice
        {
            Write = (command, _) => command.Control == DeviceControl.Depth ? Task.FromException(new IOException("write failed")) : Task.CompletedTask,
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("reset", "set 0 20 50 50 25", "reset", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        device.IsArmed.Should().BeFalse();
        error.ToString().Should().Contain("ERROR set: Could not send Depth after 2 commands: write failed. Settings may be partially applied.");
        output.ToString().Should().NotContain("OK set");
    }

    [Fact]
    public async Task FailedLiveReadDoesNotFallBackToCachedSuccess()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        device.Read = _ => Task.FromException<DeviceState>(new IOException("read failed"));
        var input = new PromptInput("status", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        output.ToString().Should().NotContain("OK status");
        error.ToString().Should().Contain("ERROR status").And.Contain("read failed");
    }

    [Fact]
    public async Task StatusAlwaysReadsLiveState()
    {
        await using var device = new TestDevice { LiveState = new(new(StrokePattern.Deeper, 5, 6, 7, 8)) };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        device.LiveState = new(StrokePattern.Insist, 1.5, 2, 3, 4);
        var input = new PromptInput("status", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Reads.Should().Be(2);
        output.ToString().Should().Contain("OK status: pattern=6 speed=1.5 stroke=2 depth=3 sensation=4.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResultColoursHighlightOnlyOutcomeKeywords(bool colour)
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var input = new PromptInput("reset", "set 0 1.5 5 5 5", "quit");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new PromptConsole(output, error, colourOutput: colour, colourError: colour);

        (await new PromptHandler(device, input, output, error, console).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

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
