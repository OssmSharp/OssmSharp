using AwesomeAssertions;
using FastMoq;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;
using OssmSharp.Runtime.Oem.Tests;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PromptCommandTests : MockerTestBase<PromptCommand>, IAsyncDisposable
{
    private readonly CommandTestContext context = new();
    private readonly PromptInput input = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly RecordingLogger<PromptCommand> logger = new();
    private Func<string, IOssmDevice> createDevice = _ => throw new InvalidOperationException("Factory must not run");

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.AddType(context.Discovery);
        mocks.AddType<Func<string, IOssmDevice>>(identifier => createDevice(identifier));
        mocks.AddType(new PromptHandlerFactory(
            input,
            output,
            error,
            new PromptConsole(output, error),
            new PromptInterruptState()));
        mocks.AddKeyedType<TextWriter>("error", error);
        mocks.AddType<ILogger<PromptCommand>>(logger);
    };

    public ValueTask DisposeAsync()
    {
        try
        {
            context.Dispose();
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
    public async Task PromptWithNoDevicesDoesNotCreateDevice()
    {
        var run = Component.RunAsync(null, TestContext.Current.CancellationToken);
        context.Clock.Advance(TimeSpan.FromSeconds(5));

        (await run).Should().Be(1);
        context.Scans.Should().Be(1);
        context.ScanDisposals.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Be($"Scanning for nearby devices...{Environment.NewLine}No devices found.{Environment.NewLine}");
    }

    [Fact]
    public async Task PromptWithoutIdentifierConnectsToFirstDiscoveredDevice()
    {
        var selectedIdentifiers = new List<string>();
        await using var device = new TestDevice();
        createDevice = selected =>
        {
            selectedIdentifiers.Add(selected);
            return device;
        };
        var run = Component.RunAsync(null, TestContext.Current.CancellationToken);
        context.Advertise("Device-A", "First OSSM");
        context.Advertise("Device-B", "Second OSSM");
        await input.NextReadAsync(TestContext.Current.CancellationToken);
        input.SendLine("quit");

        (await run).Should().Be(0);
        selectedIdentifiers.Should().Equal("Device-A");
        context.Scans.Should().Be(1);
        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0), new DeviceCommand(DeviceControl.Speed, 0));
        device.Disconnects.Should().Be(1);
        error.ToString().Should().Contain("Connected. Type 'help' for commands.").And.NotContain("Armed:");
    }

    [Fact]
    public async Task PromptUnavailableIdentifierReturnsConnectionFailureWithoutScanning()
    {
        var factoryCalls = 0;
        await using var device = new TestDevice
        {
            Connect = _ => Task.FromException(new InvalidOperationException("The selected bluetooth device is unavailable. Discover devices again before connecting.")),
        };
        createDevice = selected =>
        {
            factoryCalls++;
            selected.Should().Be("unavailable");
            return device;
        };

        (await Component.RunAsync("unavailable", TestContext.Current.CancellationToken)).Should().Be(1);
        context.Scans.Should().Be(0);
        factoryCalls.Should().Be(1);
        device.Disconnects.Should().Be(1);
        device.Writes.Should().BeEmpty();
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("ERROR prompt: The selected bluetooth device is unavailable.")
            .And.Contain("Discover devices again before connecting.").And.NotContain("Scanning").And.NotContain("Connected.");
    }

    [Fact]
    public async Task PromptDiscoveryFailureDoesNotCreateDevice()
    {
        var run = Component.RunAsync(null, TestContext.Current.CancellationToken);
        context.ScanResults.OnError(new IOException("scan failed"));

        (await run).Should().Be(1);
        context.ScanDisposals.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("ERROR discovery: scan failed Check bluetooth access and that the device is turned on.").And.NotContain("Connecting");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Device-B")]
    [InlineData("dEvIcE-b")]
    [InlineData("known-device")]
    public async Task PromptConnectsOnlyToSelectedDeviceAndArmsWithoutSettings(string? identifier)
    {
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
        createDevice = selected =>
        {
            selectedIdentifiers.Add(selected);
            return device;
        };

        var run = Component.RunAsync(identifier, TestContext.Current.CancellationToken);
        if (identifier is null)
        {
            context.Advertise("Device-A", "First OSSM");
        }

        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            connects.Should().Be(1);
            device.Reads.Should().Be(1);
            device.IsArmed.Should().BeTrue();
            device.Writes.Should().BeEmpty();
            device.Disconnects.Should().Be(0);
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            input.SendLine("quit");
            await run;
        }

        (await run).Should().Be(0);
        context.Scans.Should().Be(identifier is null ? 1 : 0);
        selectedIdentifiers.Should().Equal(identifier ?? "Device-A");
        device.Disconnects.Should().Be(1);
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0), new DeviceCommand(DeviceControl.Speed, 0));
        output.ToString().Should().Contain("OK stop").And.NotContain("OK set");
        error.ToString().Should().Contain("Connected. Type 'help' for commands.").And.Contain("Sending stop command...")
            .And.NotContain("ERROR").And.NotContain("Armed:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptDisposesAfterConnectFailureAndPreservesBothErrors(bool cleanupFails)
    {
        var cleanupFailureObserved = false;
        try
        {
            await using var device = new TestDevice
            {
                Connect = _ => Task.FromException(new IOException("connect failed")),
                Disconnect = () => cleanupFails ? Task.FromException(new IOException("disconnect failed")) : Task.CompletedTask,
            };
            createDevice = _ => device;

            (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);
            device.Disconnects.Should().Be(cleanupFails ? 2 : 1);
            device.IsArmed.Should().BeFalse();
            device.Writes.Should().BeEmpty();
            output.ToString().Should().BeEmpty();
            error.ToString().Should().Contain("ERROR prompt: ").And.Contain("connect failed").And.NotContain("Connected.");
            if (cleanupFails)
            {
                error.ToString().Should().Contain("ERROR device cleanup: disconnect failed");
            }
            else
            {
                error.ToString().Should().NotContain("ERROR device cleanup");
            }
        }
        catch (IOException exception) when (cleanupFails && exception.Message == "disconnect failed")
        {
            cleanupFailureObserved = true;
        }

        cleanupFailureObserved.Should().Be(cleanupFails);
    }

    [Fact]
    public async Task PromptDisposalFailureReturnsOne()
    {
        var cleanupFailureObserved = false;
        try
        {
            await using var device = new TestDevice
            {
                Disconnect = () => Task.FromException(new IOException("disconnect failed")),
            };
            createDevice = _ => device;
            input.SendLine("quit");

            (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);
            device.Disconnects.Should().Be(1);
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0), new DeviceCommand(DeviceControl.Speed, 0));
            error.ToString().Should().Contain("Connected. Type 'help' for commands.").And.Contain("ERROR device cleanup: disconnect failed")
                .And.NotContain("ERROR prompt").And.NotContain("Armed:");
        }
        catch (IOException exception) when (exception.Message == "disconnect failed")
        {
            cleanupFailureObserved = true;
        }

        cleanupFailureObserved.Should().BeTrue();
    }

    [Fact]
    public async Task PromptStopFailureDuringQuitReturnsOneAfterDisposal()
    {
        var failure = new IOException("Stop failed during quit.");
        await using var device = new TestDevice();
        device.Write = (_, _) => device.Writes.Count == 1 ? Task.FromException(failure) : Task.CompletedTask;
        createDevice = _ => device;
        input.SendLine("quit");

        (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);

        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0), new DeviceCommand(DeviceControl.Speed, 0));
        device.Disconnects.Should().Be(1);
        error.ToString().Should().Contain("ERROR stop: Stop failed during quit.").And.NotContain("ERROR device cleanup");
        output.ToString().Should().NotContain("OK stop");
    }

    [Fact]
    public async Task PromptDisposalStopFailureReturnsOneAndIsRepeated()
    {
        var failure = new IOException("Stop failed during disposal.");
        var assertionsCompleted = false;
        var cleanupFailureObserved = false;
        try
        {
            await using var device = new TestDevice();
            device.Write = (_, _) => device.Writes.Count == 2 ? Task.FromException(failure) : Task.CompletedTask;
            createDevice = _ => device;
            input.SendLine("quit");

            (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);

            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0), new DeviceCommand(DeviceControl.Speed, 0));
            device.Disconnects.Should().Be(1);
            output.ToString().Should().Contain("OK stop");
            error.ToString().Should().Contain("ERROR device cleanup: Stop failed during disposal.").And.NotContain("ERROR stop:");
            var repeated = () => device.DisposeAsync().AsTask();
            (await repeated.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
            device.Writes.Should().HaveCount(2);
            assertionsCompleted = true;
        }
        catch (IOException exception) when (ReferenceEquals(exception, failure))
        {
            cleanupFailureObserved = true;
        }

        assertionsCompleted.Should().BeTrue();
        cleanupFailureObserved.Should().BeTrue();
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
        createDevice = _ => device;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = Component.RunAsync("Device-A", cancellation.Token);
        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            if (cancel)
            {
                await cancellation.CancelAsync();
            }
            else
            {
                input.SendLine("quit");
            }

            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            run.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            input.SendLine("quit");
            await run;
        }

        (await run).Should().Be(0);
        device.Disconnects.Should().Be(1);
        device.Writes.Should().Equal(cancel
            ? [new DeviceCommand(DeviceControl.Speed, 0)]
            : [new DeviceCommand(DeviceControl.Speed, 0), new DeviceCommand(DeviceControl.Speed, 0)]);
        error.ToString().Should().NotContain("ERROR");
        error.ToString().Split(Environment.NewLine).Count(line => line == "Cancelled. Waiting for pending work and cleanup.")
            .Should().Be(cancel ? 1 : 0);
    }
}
