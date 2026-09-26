using OssmSharp.Cli.Commands;
using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

/// <summary>
/// Verifies device CLI parsing and forwarding of bounded simulator commands.
/// </summary>
public sealed class DeviceCommandTests
{
    [Fact]
    public void StartRequiresAnAbsoluteExistingSimulatorPath()
    {
        var command = DeviceCommand.Create((_, _) => Task.FromResult(0));

        var missing = command.Parse(["start"]);
        var relative = command.Parse(["start", "--simulator-path", "simulator"]);
        var absent = command.Parse([
            "start",
            "--simulator-path",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        ]);

        Assert.NotEmpty(missing.Errors);
        Assert.Single(relative.Errors);
        Assert.Single(absent.Errors);
    }

    [Fact]
    public async Task InvocationReturnsTwoForUsageErrorsWithoutRunningTheDevice()
    {
        var invoked = false;

        var exitCode = await DeviceCommand.InvokeAsync(
            ["start"],
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task StartForwardsTheSimulatorPath()
    {
        var path = CreateTemporaryFile();
        DeviceCommandRequest? request = null;
        try
        {
            var command = DeviceCommand.Create((value, _) =>
            {
                request = value;
                return Task.FromResult(0);
            });

            var exitCode = await command.Parse(["start", "--simulator-path", path])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.NotNull(request);
            Assert.Equal(DeviceCommandOperation.Start, request.Operation);
            Assert.Equal(Path.GetFullPath(path), request.SimulatorPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IdentifyAllowsDiscoveryWithoutAnIdButRejectsABlankId()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["identify", "--peripheral-id", " "]).Errors);
        var exitCode = await command.Parse(["identify"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Identify, request.Operation);
        Assert.Null(request.PeripheralId);
    }

    [Fact]
    public async Task IdentifyForwardsOnlyTheSelectedPeripheral()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse(["identify", "--peripheral-id", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Identify, request.Operation);
        Assert.Equal("selected-device", request.PeripheralId);
        Assert.Null(request.SimulatorPath);
    }

    [Fact]
    public async Task ReplRequiresExplicitPeripheralAndEnrolledWifiMac()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["repl"]).Errors);
        Assert.NotEmpty(command.Parse(["repl", "--peripheral-id", "device-1"]).Errors);
        Assert.NotEmpty(command.Parse(["repl", "--peripheral-id", "device-1", "--wifi-mac", "invalid"]).Errors);

        var exitCode = await command.Parse([
            "repl", "--peripheral-id", "device-1", "--wifi-mac", "AA:BB:CC:DD:EE:FF",
        ]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Repl, request.Operation);
        Assert.Equal("device-1", request.PeripheralId);
        Assert.Equal("AA:BB:CC:DD:EE:FF", request.WifiMac);
    }

    [Fact]
    public async Task ForegroundPromptRejectsMotionAndKeepsReadingUntilQuit()
    {
        using var input = new StringReader("help\nset --speed 25 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nacknowledge\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ForegroundRepl.RunAsync(
            input, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("set", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("status", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForegroundPromptReturnsOnEofAfterInvalidInput()
    {
        using var input = new StringReader("set --speed 101 --stroke 20 --depth 50 --pattern SimpleStroke\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ForegroundRepl.RunAsync(
            input, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("101", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptRefusesASecondDeviceOwner()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => null, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("owned", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForegroundPromptReportsOwnershipProvisioningFailure()
    {
        using var input = new StringReader("status\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error,
            () => throw new DirectoryNotFoundException("Provision the machine-wide device runtime directory."),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Provision the machine-wide", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptReleasesItsLeaseAfterQuit()
    {
        using var input = new StringReader("help\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        using var device = new ReadOnlyTestDevice();

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => lease, () => device, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.False(lease.CanRead);
        Assert.True(device.Disposed);
        Assert.Contains("status", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptCannotSucceedWithoutAConnectedDevice()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => lease, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(lease.CanRead);
        Assert.Contains("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForegroundPromptReportsVerifiedConnectionAndOnlyReportedFeedback()
    {
        using var input = new StringReader("status\nstop\nacknowledge\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        using var device = new ReadOnlyTestDevice();

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => lease, () => device, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("enrolled-device", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Connected", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1.0.34", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("SimpleStroke", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("No reported feedback", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, device.Writes);
        Assert.True(device.Disposed);
        Assert.False(lease.CanRead);
    }

    [Fact]
    public async Task ForegroundPromptRejectsFailedConnectionAndCleansUpBeforeReleasingOwnership()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        using var device = new ReadOnlyTestDevice(connects: false);
        device.OnDispose = () => Assert.True(lease.CanRead);

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => lease, () => device, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("connection", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(device.Disposed);
        Assert.False(lease.CanRead);
    }

    [Fact]
    public async Task ForegroundPromptNeverCreatesADeviceWithoutOwnership()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => null,
            () => throw new InvalidOperationException("Device was constructed without a lease."),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("owned", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForegroundPromptDoesNotReleaseLeaseWhenConnectionCleanupFails()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        using var device = new ReadOnlyTestDevice { FailDispose = true };

        var failure = await Assert.ThrowsAsync<IOException>(() => ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => lease, () => device, TestContext.Current.CancellationToken));

        Assert.Contains("did not settle", failure.Message, StringComparison.Ordinal);
        Assert.True(lease.CanRead);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task ForegroundPromptRetainsTheFirstValidatedReportDuringConnection()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        using var device = new ReadOnlyTestDevice
        {
            InitialReport = new DeviceState
            {
                Timestamp = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc),
                MotionSettings = new MotionSettings(MotionDeviceState.StrokeEngineIdle, 25, 20, 50, 60, 0, MotionPattern.SimpleStroke, 10),
                SessionId = Guid.Empty,
            },
        };

        var exitCode = await ForegroundRepl.RunWithOwnershipAsync(
            input, output, error, () => lease, () => device, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("2026-09-26T12:00:00", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("effective speed 25%", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("velocity unknown", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task ForegroundPromptReleasesItsLeaseOnEofAndCancellation()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var eofLease = new MemoryStream();
        using var eofInput = new StringReader(string.Empty);
        using var eofDevice = new ReadOnlyTestDevice();

        Assert.Equal(0, await ForegroundRepl.RunWithOwnershipAsync(
            eofInput, output, error, () => eofLease, () => eofDevice, TestContext.Current.CancellationToken));
        Assert.False(eofLease.CanRead);
        Assert.True(eofDevice.Disposed);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var canceledLease = new MemoryStream();
        using var canceledInput = new StringReader("status\n");
        using var canceledDevice = new ReadOnlyTestDevice();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundRepl.RunWithOwnershipAsync(
            canceledInput, output, error, () => canceledLease, () => canceledDevice, cancellation.Token));
        Assert.False(canceledLease.CanRead);
        Assert.True(canceledDevice.Disposed);
    }

    [Fact]
    public async Task InterruptedForegroundCommandReturnsCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await DeviceCommand.InvokeAsync(
            ["repl", "--peripheral-id", "device-1", "--wifi-mac", "AA:BB:CC:DD:EE:FF"],
            (_, token) => Task.FromCanceled<int>(token),
            cancellation.Token);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task IdentifyPromptsForANumberAndSelectsOnlyThatLocator()
    {
        using var input = new StringReader("2\n");
        using var prompt = new StringWriter();

        var selected = await DeviceSelection.SelectAsync(
            [("Device One", "id-one"), ("Device Two", "id-two")],
            input,
            prompt,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("id-two", selected);
        Assert.Contains("1. Device One (id-one)", prompt.ToString(), StringComparison.Ordinal);
        Assert.Contains("2. Device Two (id-two)", prompt.ToString(), StringComparison.Ordinal);
        Assert.Contains("Select a device by number and press Enter:", prompt.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0\n")]
    [InlineData("3\n")]
    [InlineData("no\n")]
    [InlineData("")]
    public async Task IdentifyRejectsAnInvalidSelection(string response)
    {
        using var input = new StringReader(response);
        using var prompt = new StringWriter();

        await Assert.ThrowsAsync<ArgumentException>(() => DeviceSelection.SelectAsync(
            [("Device One", "id-one"), ("Device Two", "id-two")],
            input,
            prompt,
            true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IdentifyRefusesToPromptWithoutAnInteractiveTerminal()
    {
        using var input = new StringReader("1\n");
        using var prompt = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceSelection.SelectAsync(
            [("Device One", "id-one")],
            input,
            prompt,
            false,
            TestContext.Current.CancellationToken));

        Assert.Contains("--peripheral-id", exception.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, prompt.ToString());
    }

    [Fact]
    public async Task IdentifyReportsAnEmptyScanWithoutPrompting()
    {
        using var input = new StringReader("1\n");
        using var prompt = new StringWriter();

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceSelection.SelectAsync(
            [], input, prompt, true, TestContext.Current.CancellationToken));

        Assert.Equal(string.Empty, prompt.ToString());
    }

    [Fact]
    public async Task IdentifyDoesNotAllowAdvertisedNamesToAddTerminalLines()
    {
        using var input = new StringReader("1\n");
        using var prompt = new StringWriter();

        await DeviceSelection.SelectAsync(
            [("Other\nDevice", "id-one")], input, prompt, true, TestContext.Current.CancellationToken);

        Assert.Contains("1. Other Device (id-one)", prompt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\nDevice", prompt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifyDoesNotUseTheSimulatorHost()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var endpoint = SimulatorEndpoint.CreateDefault();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Identify, PeripheralId: "selected-device"),
            output,
            error,
            endpoint,
            (_, _) => throw new InvalidOperationException("Simulator startup is forbidden."),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("macOS", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData("status", "Status")]
    [InlineData("stop", "Stop")]
    public async Task LifecycleCommandsTargetTheRunningHost(
        string commandName,
        string operation)
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse([commandName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(operation, request.Operation.ToString());
        Assert.Null(request.SimulatorPath);
    }

    [Fact]
    public async Task MoveForwardsValidatedBoundedMotion()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse([
            "move",
            "--speed", "25",
            "--stroke", "30",
            "--depth", "60",
            "--seconds", "1.5",
        ]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Move, request.Operation);
        Assert.Null(request.SimulatorPath);
        Assert.Equal(25, request.Speed);
        Assert.Equal(30, request.Stroke);
        Assert.Equal(60, request.Depth);
        Assert.Equal(TimeSpan.FromSeconds(1.5), request.Duration);
    }

    [Theory]
    [InlineData("0", "20", "50", "1")]
    [InlineData("101", "20", "50", "1")]
    [InlineData("20", "0", "50", "1")]
    [InlineData("20", "60", "50", "1")]
    [InlineData("20", "20", "50", "0")]
    [InlineData("20", "20", "50", "61")]
    public void MoveRejectsUnsafeOrUnsupportedValues(string speed, string stroke, string depth, string seconds)
    {
        var parse = DeviceCommand.Create((_, _) => Task.FromResult(0)).Parse([
            "move",
            "--speed", speed,
            "--stroke", stroke,
            "--depth", depth,
            "--seconds", seconds,
        ]);

        Assert.NotEmpty(parse.Errors);
    }

    private static string CreateTemporaryFile()
    {
        var path = Path.GetTempFileName();
        return Path.GetFullPath(path);
    }
}
