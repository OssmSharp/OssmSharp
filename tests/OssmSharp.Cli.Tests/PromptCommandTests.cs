using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Logging;
using Moq;
using OssmSharp.Runtime.Oem;
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
        var device = Mock.Get(mocks.GetOrCreateMock<IOssmDevice>().Instance);
        device.Setup(value => value.ConnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(value => value.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
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

    private Mock<IOssmDevice> Device => Mock.Get(Mocks.GetOrCreateMock<IOssmDevice>().Instance);

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
        createDevice = selected =>
        {
            selectedIdentifiers.Add(selected);
            return Device.Object;
        };
        var run = Component.RunAsync(null, TestContext.Current.CancellationToken);
        context.Advertise("Device-A", "First OSSM");
        context.Advertise("Device-B", "Second OSSM");
        await input.NextReadAsync(TestContext.Current.CancellationToken);
        input.SendLine("quit");

        (await run).Should().Be(0);
        selectedIdentifiers.Should().Equal("Device-A");
        context.Scans.Should().Be(1);
        Mocks.Verify<IOssmDevice>(device => device.ConnectAsync(TestContext.Current.CancellationToken), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        error.ToString().Should().Contain("Connected. Type 'help' for commands.").And.NotContain("Armed:");
    }

    [Fact]
    public async Task PromptUnavailableIdentifierReturnsConnectionFailureWithoutScanning()
    {
        var factoryCalls = 0;
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The selected bluetooth device is unavailable. Discover devices again before connecting."));
        createDevice = selected =>
        {
            factoryCalls++;
            selected.Should().Be("unavailable");
            return Device.Object;
        };

        (await Component.RunAsync("unavailable", TestContext.Current.CancellationToken)).Should().Be(1);
        context.Scans.Should().Be(0);
        factoryCalls.Should().Be(1);
        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
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
    public async Task PromptConnectsOnlyToSelectedDeviceWithoutSettings(string? identifier)
    {
        var selectedIdentifiers = new List<string>();
        createDevice = selected =>
        {
            selectedIdentifiers.Add(selected);
            return Device.Object;
        };

        var run = Component.RunAsync(identifier, TestContext.Current.CancellationToken);
        if (identifier is null)
        {
            context.Advertise("Device-A", "First OSSM");
        }

        try
        {
            await input.NextReadAsync(TestContext.Current.CancellationToken);
            Mocks.Verify<IOssmDevice>(device => device.ConnectAsync(TestContext.Current.CancellationToken), TimesSpec.Once);
            Mocks.Verify<IOssmDevice>(device => device.GetStateAsync(FastArg.Any<bool>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            Device.Verify(device => device.DisposeAsync(), Times.Never);
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
        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        output.ToString().Should().Contain("OK stop").And.NotContain("OK set");
        error.ToString().Should().Contain("Connected. Type 'help' for commands.").And.Contain("Sending stop command...")
            .And.NotContain("ERROR").And.NotContain("Armed:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptDisposesAfterConnectFailureAndPreservesBothErrors(bool cleanupFails)
    {
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("connect failed"));
        if (cleanupFails)
        {
            Device.Setup(device => device.DisposeAsync()).Returns(() => ValueTask.FromException(new IOException("disconnect failed")));
        }

        createDevice = _ => Device.Object;

        (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);

        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
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

    [Fact]
    public async Task PromptDisposalFailureReturnsOne()
    {
        Device.Setup(device => device.DisposeAsync()).Returns(() => ValueTask.FromException(new IOException("disconnect failed")));
        createDevice = _ => Device.Object;
        input.SendLine("quit");

        (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);

        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        error.ToString().Should().Contain("Connected. Type 'help' for commands.").And.Contain("ERROR device cleanup: disconnect failed")
            .And.NotContain("ERROR prompt").And.NotContain("Armed:");
    }

    [Fact]
    public async Task PromptStopFailureDuringQuitReturnsOneAfterDisposal()
    {
        var failure = new IOException("Stop failed during quit.");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        createDevice = _ => Device.Object;
        input.SendLine("quit");

        (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        Device.Verify(device => device.DisposeAsync(), Times.Once);
        error.ToString().Should().Contain("ERROR stop: Stop failed during quit.").And.NotContain("ERROR device cleanup");
        output.ToString().Should().NotContain("OK stop");
    }

    [Fact]
    public async Task PromptCleanupFailurePreservesCompletedStopOutput()
    {
        var failure = new IOException("Stop failed during disposal.");
        Device.Setup(device => device.DisposeAsync()).Returns(() => ValueTask.FromException(failure));
        createDevice = _ => Device.Object;
        input.SendLine("quit");

        (await Component.RunAsync("Device-A", TestContext.Current.CancellationToken)).Should().Be(1);

        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Once);
        output.ToString().Should().Contain("OK stop");
        error.ToString().Should().Contain("ERROR device cleanup: Stop failed during disposal.").And.NotContain("ERROR stop:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAwaitsDelayedDisposalEvenAfterCancellation(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.DisposeAsync()).Returns(() =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        });
        createDevice = _ => Device.Object;
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
        Device.Verify(device => device.DisposeAsync(), Times.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(CancellationToken.None), TimesSpec.Exactly(cancel ? 0 : 1));
        error.ToString().Should().NotContain("ERROR");
        error.ToString().Split(Environment.NewLine).Count(line => line == "Cancelled. Waiting for pending work and cleanup.")
            .Should().Be(cancel ? 1 : 0);
    }
}
