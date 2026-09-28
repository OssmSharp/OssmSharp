using AwesomeAssertions;
using OssmSharp.Runtime.Oem;
using OssmSharp.Runtime.Oem.Tests;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task MainReturnsFailureEvenWhenDiagnosticsAreUnavailable()
    {
        using var output = new StringWriter();
        using var error = new FailingWriter(_ => true);

        (await Program.InvokeAsync(["--unknown"], output, error)).Should().Be(1);
        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "--help" }, "list")]
    [InlineData(new[] { "list", "--help" }, "identifiers")]
    [InlineData(new[] { "prompt", "--help" }, "--device")]
    public async Task MainHelpReturnsZeroWithoutDeviceAccess(string[] args, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await Program.InvokeAsync(args, output, error)).Should().Be(0);

        output.ToString().Should().Contain(expected);
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task MainVersionReturnsZeroWithoutDeviceAccess()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await Program.InvokeAsync(["--version"], output, error)).Should().Be(0);

        output.ToString().Trim().Should().MatchRegex(@"^\d+\.\d+\.\d+[^\r\n]*$");
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "unknown" }, "unknown")]
    [InlineData(new[] { "--unknown" }, "--unknown")]
    [InlineData(new[] { "list", "--unknown" }, "--unknown")]
    [InlineData(new[] { "prompt", "--device" }, "--device")]
    [InlineData(new[] { "prompt", "--device", "" }, "nonempty identifier")]
    [InlineData(new[] { "prompt", "--device", " " }, "nonempty identifier")]
    public async Task MainInvalidArgumentsReturnTwoWithoutDeviceAccess(string[] args, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await Program.InvokeAsync(args, output, error)).Should().Be(2);

        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("ERROR:").And.Contain("--help").And.Contain(expected)
            .And.NotContain("Searching").And.NotContain("Connecting");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ListWritesIdentifiersAndNamesWithProgressOnStderr(int count)
    {
        DiscoveredDevice[] available = [new("Device-A", "First OSSM"), new("Device-B", "Second OSSM")];
        var devices = available.Take(count).ToArray();
        var scans = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await Program.ListAsync(
            token =>
            {
                token.Should().Be(TestContext.Current.CancellationToken);
                scans++;
                return Task.FromResult<IReadOnlyList<DiscoveredDevice>>(devices);
            },
            output,
            error,
            TestContext.Current.CancellationToken);

        result.Should().Be(0);
        scans.Should().Be(1);
        var expected = count switch
        {
            0 => string.Empty,
            1 => $"Device-A  First OSSM{Environment.NewLine}",
            _ => $"Device-A  First OSSM{Environment.NewLine}Device-B  Second OSSM{Environment.NewLine}",
        };
        output.ToString().Should().Be(expected);
        error.ToString().Should().Be($"Searching for OSSM devices...{Environment.NewLine}");
    }

    [Fact]
    public async Task ListDiscoveryFailureReturnsOne()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await Program.ListAsync(
            _ => Task.FromException<IReadOnlyList<DiscoveredDevice>>(new IOException("scan failed")),
            output,
            error,
            TestContext.Current.CancellationToken);

        result.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("Searching").And.Contain("ERROR discovery: scan failed").And.Contain("Bluetooth access");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task PromptRejectsAmbiguousSelectionWithoutCreatingDevice(int count)
    {
        DiscoveredDevice[] available = [new("Device-A", "First OSSM"), new("Device-B", "Second OSSM")];
        var factoryCalls = 0;
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await Program.PromptAsync(
            _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>(available.Take(count).ToArray()),
            _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Factory must not run");
            },
            null,
            input,
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be(1);
        factoryCalls.Should().Be(0);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain($"ERROR selection: found {count} OSSM devices")
            .And.Contain("No connection made").And.Contain("--device <id>").And.NotContain("Connecting");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PromptRejectsUnavailableIdentifierWithoutCreatingDevice(int count)
    {
        DiscoveredDevice[] available = [new("Device-A", "First OSSM"), new("Device-B", "Second OSSM")];
        var factoryCalls = 0;
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await Program.PromptAsync(
            _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>(available.Take(count).ToArray()),
            _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Factory must not run");
            },
            "unavailable",
            input,
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be(1);
        factoryCalls.Should().Be(0);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("ERROR selection: selected device is unavailable")
            .And.Contain("No connection made").And.Contain("--device <id>").And.NotContain("Connecting");
    }

    [Fact]
    public async Task PromptDiscoveryFailureDoesNotCreateDevice()
    {
        var factoryCalls = 0;
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await Program.PromptAsync(
            _ => Task.FromException<IReadOnlyList<DiscoveredDevice>>(new IOException("scan failed")),
            _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Factory must not run");
            },
            null,
            input,
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be(1);
        factoryCalls.Should().Be(0);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("ERROR prompt: scan failed").And.NotContain("Connecting");
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("Device-B", 2)]
    [InlineData("dEvIcE-b", 2)]
    public async Task PromptConnectsOnlyToSelectedDeviceWithoutArmingOrSettings(string? identifier, int count)
    {
        DiscoveredDevice[] available = [new("Device-A", "First OSSM"), new("Device-B", "Second OSSM")];
        var selectedIdentifiers = new List<string>();
        var connects = 0;
        await using var device = new TestDevice
        {
            Connect = _ =>
            {
                connects++;
                return Task.CompletedTask;
            },
        };
        var input = new CommandInput();
        var output = new StringWriter();
        var error = new StringWriter();
        var run = Program.PromptAsync(
            token =>
            {
                token.Should().Be(TestContext.Current.CancellationToken);
                return Task.FromResult<IReadOnlyList<DiscoveredDevice>>(available.Take(count).ToArray());
            },
            selected =>
            {
                selectedIdentifiers.Add(selected);
                return device;
            },
            identifier,
            input,
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            connects.Should().Be(1);
            device.Reads.Should().Be(1);
            device.IsArmed.Should().BeFalse();
            device.Writes.Should().BeEmpty();
            device.Disconnects.Should().Be(0);
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            input.Send("quit");
            try
            {
                await run;
            }
            finally
            {
                input.Dispose();
                await output.DisposeAsync();
                await error.DisposeAsync();
            }
        }

        (await run).Should().Be(0);
        selectedIdentifiers.Should().Equal(count == 1 ? "Device-A" : "Device-B");
        device.Disconnects.Should().Be(1);
        device.Writes.Should().BeEmpty();
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("Connected. Armed: no").And.NotContain("ERROR");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptDisposesAfterConnectFailureAndPreservesBothErrors(bool cleanupFails)
    {
        TestDevice? device = null;
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = await Program.PromptAsync(
                _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>([new("Device-A", "First OSSM")]),
                _ => device = new TestDevice
                {
                    Connect = _ => Task.FromException(new IOException("connect failed")),
                    Disconnect = () => cleanupFails ? Task.FromException(new IOException("disconnect failed")) : Task.CompletedTask,
                },
                null,
                input,
                output,
                error,
                cancellationToken: TestContext.Current.CancellationToken);

            result.Should().Be(1);
            device.Should().NotBeNull();
            device!.Disconnects.Should().Be(1);
            device.IsArmed.Should().BeFalse();
            device.Writes.Should().BeEmpty();
            output.ToString().Should().BeEmpty();
            error.ToString().Should().Contain("ERROR prompt: connect failed").And.NotContain("Connected.");
            if (cleanupFails)
            {
                error.ToString().Should().Contain("ERROR device cleanup: disconnect failed");
            }
            else
            {
                error.ToString().Should().NotContain("ERROR device cleanup");
            }
        }
        finally
        {
            try
            {
                if (device is not null)
                {
                    await device.DisposeAsync();
                }
            }
            catch (IOException exception) when (cleanupFails && exception.Message == "disconnect failed")
            {
            }
        }
    }

    [Fact]
    public async Task PromptDisposalFailureReturnsOne()
    {
        TestDevice? device = null;
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            var result = await Program.PromptAsync(
                _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>([new("Device-A", "First OSSM")]),
                _ => device = new TestDevice
                {
                    Disconnect = () => Task.FromException(new IOException("disconnect failed")),
                },
                null,
                input,
                output,
                error,
                cancellationToken: TestContext.Current.CancellationToken);

            result.Should().Be(1);
            device.Should().NotBeNull();
            device!.Disconnects.Should().Be(1);
            device.Writes.Should().BeEmpty();
            error.ToString().Should().Contain("Connected. Armed: no").And.Contain("ERROR device cleanup: disconnect failed")
                .And.NotContain("ERROR prompt");
        }
        finally
        {
            if (device is not null)
            {
                Func<Task> cleanup = () => device.DisposeAsync().AsTask();
                await cleanup.Should().ThrowAsync<IOException>().WithMessage("disconnect failed");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAwaitsDelayedDisposalEvenAfterCancellation(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice
        {
            Disconnect = () =>
            {
                entered.TrySetResult();
                return release.Task;
            },
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var input = new CommandInput();
        var output = new StringWriter();
        var error = new StringWriter();
        var run = Program.PromptAsync(
            _ => Task.FromResult<IReadOnlyList<DiscoveredDevice>>([new("Device-A", "First OSSM")]),
            _ => device,
            null,
            input,
            output,
            error,
            cancellationToken: cancellation.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            if (cancel)
            {
                await cancellation.CancelAsync();
            }
            else
            {
                input.Send("quit");
            }

            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            input.Send("quit");
            try
            {
                await run;
            }
            finally
            {
                input.Dispose();
                await output.DisposeAsync();
                await error.DisposeAsync();
            }
        }

        (await run).Should().Be(0);
        device.Disconnects.Should().Be(1);
        device.Writes.Should().BeEmpty();
        error.ToString().Should().NotContain("ERROR");
    }
}
