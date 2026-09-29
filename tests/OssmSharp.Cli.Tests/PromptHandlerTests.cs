using AwesomeAssertions;
using OssmSharp.Runtime.Oem;
using OssmSharp.Runtime.Oem.Tests;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PromptHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofAndCancellationAwaitIssuedSettings(bool cancel)
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
        using var input = new CommandInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = new PromptHandler(device, input, output, error).RunAsync(cancellation.Token);
        try
        {
            input.Send("reset");
            input.Send("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            for (var count = 0; count < 3; count++)
            {
                await input.NextReadAsync(TestContext.Current.CancellationToken);
            }

            if (cancel)
            {
                await cancellation.CancelAsync();
            }
            else
            {
                input.Send(null);
            }

            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            await run;
        }

        (await run).Should().Be(0);
        device.Writes.Should().Equal(cancel
            ? [new DeviceCommand(DeviceControl.Pattern, 0)]
            : [new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0)]);
        output.ToString().Should().Contain("CANCELLED set: 1 commands sent");
    }

    [Fact]
    public async Task SaturatedAdmissionStillAcceptsAndCoalescesStop()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Write = (command, _) =>
            {
                if (command.Control == DeviceControl.Speed)
                {
                    return release.Task;
                }

                if (command.Control != DeviceControl.Pattern)
                {
                    return Task.CompletedTask;
                }

                entered.TrySetResult();
                return release.Task;
            },
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new CommandInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);
        try
        {
            input.Send("reset");
            input.Send("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            for (var count = 0; count < 35; count++)
            {
                input.Send("status --live");
            }

            input.Send("stop");
            input.Send("stop");
            for (var count = 0; count < 40; count++)
            {
                await input.NextReadAsync(TestContext.Current.CancellationToken);
            }

            device.IsArmed.Should().BeFalse();
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0));
            error.ToString().Should().Contain("queue is full").And.Contain("Stop already in progress");
        }
        finally
        {
            input.Send("quit");
            release.TrySetResult();
            await run;
        }

        (await run).Should().Be(0);

        // Quit joins the Stop if it is still in progress; otherwise it sends another.
        device.Writes.Take(2).Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0));
        device.Writes.Skip(2).Should().AllBeEquivalentTo(new DeviceCommand(DeviceControl.Speed, 0)).And.HaveCountLessThan(2);
    }

    [Fact]
    public async Task StopAwaitsItsWriteEvenWhenProgressOutputFails()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice { Write = (_, _) => release.Task };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new StringReader("stop\nquit\n");
        using var output = new StringWriter();
        using var error = new FailingWriter(line => line?.StartsWith("Stopping", StringComparison.Ordinal) == true);
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);
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
        using var input = new StringReader("reset\nset 0 20 50 50 25\nquit\n");
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
        using var input = new StringReader("status --live\nquit\n");
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
        using var input = new StringReader("status --live\nquit\n");
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
    [InlineData("set -1 20 50 50 25", "pattern '-1'")]
    [InlineData("set 1.5 20 50 50 25", "pattern '1.5'")]
    [InlineData("set 2147483648 20 50 50 25", "pattern '2147483648'")]
    [InlineData("set 7 20 50 50 25", "pattern '7'")]
    [InlineData("set 01 20 50 50 25", "pattern '01'")]
    [InlineData("set Deeper,Insist 20 50 50 25", "pattern 'Deeper,Insist'")]
    [InlineData("set 0 20 50", "depth")]
    [InlineData("set 0 20 50 50 25 extra", "extra")]
    [InlineData("status --fresh", "--fresh")]
    public async Task InvalidInputSendsNothingAndSessionContinues(string command, string offending)
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new StringReader($"reset\n{command}\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new PromptHandler(device, input, output, error);

        (await handler.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        error.ToString().Should().Contain(offending).And.Contain("Nothing sent").And.Contain("Armed: yes");
        output.ToString().Should().Contain("OK status (cached)").And.NotContain("OK set");
    }

    [Fact]
    public async Task SettingsAfterConnectionNeedNoReset()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new StringReader("set 0 20 50 50 25\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Writes.Should().HaveCount(6).And.EndWith(new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("OK set").And.Contain("Armed: yes");
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
        using var input = new StringReader("quit\nset 0 20 50 50 25\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);

        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            run.IsCompleted.Should().BeFalse();
            error.ToString().Should().Contain("Sending stop command. Please wait ...");
        }
        finally
        {
            release.TrySetResult();
        }

        (await run).Should().Be(0);
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        device.IsArmed.Should().BeFalse();
        output.ToString().Should().Contain("OK stop");
    }

    [Fact]
    public async Task CommandsPreserveRuntimeOrderingAndStateOrigin()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new StringReader("stop\nset 0 20 50 50 25\nreset\nset insist 100 0 50.5 25\nstatus\nstatus --live\nhelp\nhelp set\nhelp bogus\nstop\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        device.Writes.Should().Equal(
            new DeviceCommand(DeviceControl.Speed, 0),
            new DeviceCommand(DeviceControl.Pattern, 6),
            new DeviceCommand(DeviceControl.Stroke, 0),
            new DeviceCommand(DeviceControl.Depth, 50.5),
            new DeviceCommand(DeviceControl.Sensation, 25),
            new DeviceCommand(DeviceControl.Speed, 100),
            new DeviceCommand(DeviceControl.Speed, 0),
            new DeviceCommand(DeviceControl.Speed, 0));
        device.Reads.Should().Be(2);
        device.IsArmed.Should().BeFalse();
        error.ToString().Should().Contain("Nothing sent").And.Contain("disarmed").And.Contain("'bogus'");
        output.ToString().Should().Contain("OK reset").And.Contain("No motion command sent")
            .And.Contain("OK set").And.Contain("physical motion not verified")
            .And.Contain("status (cached): pattern=0 speed=0")
            .And.Contain("status (live): pattern=0 speed=0")
            .And.Contain("set <pattern> <speed> <stroke> <depth> <sensation>")
            .And.Contain("0=SimpleStroke, 1=TeasingPounding, 2=RoboStroke, 3=HalfNHalf, 4=Deeper, 5=StopNGo, 6=Insist")
            .And.Contain("OK stop").And.Contain("Armed: no");
    }

    [Fact]
    public async Task QuitSendsStopImmediatelyAndWaitsForCommandInProgress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Write = (command, token) =>
            {
                token.CanBeCanceled.Should().BeFalse();
                if (command.Control == DeviceControl.Pattern)
                {
                    entered.TrySetResult();
                    return release.Task;
                }

                stopSent.TrySetResult();
                return Task.CompletedTask;
            },
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new CommandInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);
        try
        {
            input.Send("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            input.Send("quit");
            await stopSent.Task.WaitAsync(TestContext.Current.CancellationToken);
            device.IsArmed.Should().BeFalse();
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0));
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await run;
        }

        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 0), new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("CANCELLED set: 1 commands sent").And.Contain("OK stop");
        error.ToString().Should().Contain("Sending stop command. Please wait ...");
    }

    [Fact]
    public async Task NewSettingsSupersedePendingCommandsWithoutAbandoningIssuedIo()
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
        using var input = new CommandInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var run = new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken);
        try
        {
            input.Send("reset");
            input.Send("set 0 20 50 50 25");
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            input.Send("set 1 30 60 40 35");
            for (var count = 0; count < 4; count++)
            {
                await input.NextReadAsync(TestContext.Current.CancellationToken);
            }

            device.Writes.Should().ContainSingle();
            release.TrySetResult();
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
            input.Send("quit");
            await run;
        }

        device.Writes.Should().Equal(
            new DeviceCommand(DeviceControl.Pattern, 0),
            new DeviceCommand(DeviceControl.Pattern, 1),
            new DeviceCommand(DeviceControl.Stroke, 60),
            new DeviceCommand(DeviceControl.Depth, 40),
            new DeviceCommand(DeviceControl.Sensation, 35),
            new DeviceCommand(DeviceControl.Speed, 30),
            new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("SUPERSEDED set: 1 commands sent");
    }

    [Fact]
    public async Task IdleConnectionFailureCancelsInputAndEndsSession()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        using var input = new CommandInput();
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
        using var input = new StringReader("reset\nset 0 20 50 50 25\nreset\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        device.IsArmed.Should().BeFalse();
        error.ToString().Should().Contain("Depth write failed after 2 completed commands").And.Contain("partially applied");
        output.ToString().Should().NotContain("OK set");
    }

    [Fact]
    public async Task FailedLiveReadDoesNotFallBackToCachedSuccess()
    {
        await using var device = new TestDevice();
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        device.Read = _ => Task.FromException<DeviceState>(new IOException("read failed"));
        using var input = new StringReader("status --live\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await new PromptHandler(device, input, output, error).RunAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        output.ToString().Should().NotContain("OK status");
        error.ToString().Should().Contain("ERROR status").And.Contain("read failed");
    }
}
