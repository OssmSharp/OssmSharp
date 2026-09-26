using OssmSharp.Cli.Commands;
using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

/// <summary>
/// Verifies device CLI parsing, discovery and foreground ownership.
/// </summary>
public sealed class DeviceCommandTests
{
    [Fact]
    public void OnlyDeviceAndPromptCommandsAreExposed()
    {
        var command = CreateRoot(new PromptCommand((_, _, _, _) => Task.FromResult(0)));

        Assert.Equal(["device", "prompt"], command.Subcommands.Select(child => child.Name));
        foreach (var name in new[] { "simulator", "start-simulator", "status", "identify", "scan", "move", "stop" })
        {
            Assert.NotEmpty(command.Parse([name]).Errors);
        }
    }

    [Fact]
    public void SimulatorPromptRequiresAnAbsoluteExistingExecutable()
    {
        var command = CreateRoot(new PromptCommand((_, _, _, _) => Task.FromResult(0)));

        var missing = command.Parse(["prompt"]);
        var relative = command.Parse(["prompt", "--simulator", "simulator"]);
        var absent = command.Parse([
            "prompt", "--simulator",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        ]);

        Assert.NotEmpty(missing.Errors);
        Assert.NotEmpty(relative.Errors);
        Assert.NotEmpty(absent.Errors);
    }

    [Fact]
    public async Task InvalidPromptDoesNotRunTheDevice()
    {
        var invoked = false;
        var command = CreateRoot(new PromptCommand((_, _, _, _) =>
        {
            invoked = true;
            return Task.FromResult(0);
        }));

        var parse = command.Parse(["prompt", "--peripheral-id", "selected-device"]);
        await parse.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(parse.Errors);
        Assert.False(invoked);
    }

    [Fact]
    public async Task PromptForwardsTheSimulatorPath()
    {
        var path = CreateTemporaryFile();
        string? selectedPath = null;
        try
        {
            var command = CreateRoot(new PromptCommand((_, _, simulator, _) =>
            {
                selectedPath = simulator;
                return Task.FromResult(0);
            }));

            var exitCode = await command.Parse(["prompt", "--simulator", path])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(Path.GetFullPath(path), selectedPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IdentifyAllowsDiscoveryWithoutAnIdButRejectsABlankId()
    {
        string? selectedId = "not-called";
        var command = new DeviceGetCommand((peripheralId, _) =>
        {
            selectedId = peripheralId;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["get", "--peripheral-id", " "]).Errors);
        var exitCode = await command.Parse(["get"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Null(selectedId);
    }

    [Fact]
    public async Task IdentifyForwardsOnlyTheSelectedPeripheral()
    {
        string? selectedId = null;
        var command = new DeviceGetCommand((peripheralId, _) =>
        {
            selectedId = peripheralId;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse(["get", "--peripheral-id", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("selected-device", selectedId);
    }

    [Fact]
    public async Task ScanNeedsNoEnrolledIdentity()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var lease = new MemoryStream();
        var command = new DeviceListCommand(() => lease,
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([]), output, error);

        var exitCode = await command.Parse(["list"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("No stock devices found", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ScanPrintsDiscoveredLocatorsAndReleasesOwnership()
    {
        using var lease = new MemoryStream();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var command = new DeviceListCommand(
            () => lease,
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Found device", "platform-id")]),
            output, error);

        var exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Found device (platform-id)", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.False(lease.CanRead);
    }

    [Fact]
    public async Task ScanRefusesAnotherOwnerBeforeStartingBluetooth()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var scanned = false;

        var command = new DeviceListCommand(
            () => null,
            _ =>
            {
                scanned = true;
                return Task.FromResult<IReadOnlyList<(string Name, string Id)>>([]);
            },
            output, error);

        var exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(scanned);
        Assert.Contains("owned", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplRequiresExplicitPeripheralAndEnrolledWifiMac()
    {
        string? selectedId = null;
        string? selectedMac = null;
        var command = new PromptCommand((peripheralId, wifiMac, _, _) =>
        {
            selectedId = peripheralId;
            selectedMac = wifiMac;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["prompt"]).Errors);
        Assert.NotEmpty(command.Parse(["prompt", "--peripheral-id", "device-1"]).Errors);
        Assert.NotEmpty(command.Parse(["prompt", "--peripheral-id", "device-1", "--wifi-mac", "invalid"]).Errors);

        var exitCode = await command.Parse([
            "prompt", "--peripheral-id", "device-1", "--wifi-mac", "AA:BB:CC:DD:EE:FF",
        ]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("device-1", selectedId);
        Assert.Equal("AA:BB:CC:DD:EE:FF", selectedMac);
    }

    [Fact]
    public async Task ForegroundPromptRejectsMotionAndKeepsReadingUntilQuit()
    {
        using var input = new StringReader("help\nset --speed 25 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nacknowledge\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error, () => null);
        var exitCode = await handler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("set", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("status", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stop unavailable", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptReturnsOnEofAfterInvalidInput()
    {
        using var input = new StringReader("set --speed 101 --stroke 20 --depth 50 --pattern SimpleStroke\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error, () => null);
        var exitCode = await handler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("101", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptRefusesASecondDeviceOwner()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error, () => null);
        var exitCode = await handler.RunWithOwnershipAsync(TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error,
            () => throw new DirectoryNotFoundException("Provision the machine-wide device runtime directory."));
        var exitCode = await handler.RunWithOwnershipAsync(TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error, () => lease);
        var exitCode = await handler.RunWithOwnershipAsync(() => device, TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error, () => lease);
        var exitCode = await handler.RunWithOwnershipAsync(TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error, () => lease);
        var command = new PromptCommand((_, _, _, cancellationToken) =>
            handler.RunWithOwnershipAsync(() => device, cancellationToken));
        var exitCode = await command.Parse([
            "prompt", "--peripheral-id", "enrolled-device", "--wifi-mac", "AA:BB:CC:DD:EE:FF",
        ]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error, () => lease);
        var exitCode = await handler.RunWithOwnershipAsync(() => device, TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error, () => null);
        var exitCode = await handler.RunWithOwnershipAsync(
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

        var handler = new PromptHandler(input, output, error, () => lease);
        var failure = await Assert.ThrowsAsync<IOException>(() => handler.RunWithOwnershipAsync(
            () => device, TestContext.Current.CancellationToken));

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

        var handler = new PromptHandler(input, output, error, () => lease);
        var exitCode = await handler.RunWithOwnershipAsync(() => device, TestContext.Current.CancellationToken);

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

        var eofHandler = new PromptHandler(eofInput, output, error, () => eofLease);
        Assert.Equal(0, await eofHandler.RunWithOwnershipAsync(
            () => eofDevice, TestContext.Current.CancellationToken));
        Assert.False(eofLease.CanRead);
        Assert.True(eofDevice.Disposed);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var canceledLease = new MemoryStream();
        using var canceledInput = new StringReader("status\n");
        using var canceledDevice = new ReadOnlyTestDevice();

        var canceledHandler = new PromptHandler(canceledInput, output, error, () => canceledLease);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledHandler.RunWithOwnershipAsync(
            () => canceledDevice, cancellation.Token));
        Assert.False(canceledLease.CanRead);
        Assert.True(canceledDevice.Disposed);
    }

    [Fact]
    public async Task InterruptedForegroundCommandReturnsCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await Program.InvokeAsync(
            CreateRoot(new PromptCommand((_, _, _, token) => Task.FromCanceled<int>(token))),
            ["prompt", "--peripheral-id", "device-1", "--wifi-mac", "AA:BB:CC:DD:EE:FF"],
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

    [Fact]
    public async Task ScanPrintsPlatformIdsWithoutPrompting()
    {
        using var output = new StringWriter();
        using var lease = new MemoryStream();
        var command = new DeviceListCommand(() => lease,
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Other\nDevice", "platform-id-one"), ("Device Two", "platform-id-two")]),
            output, TextWriter.Null);

        await command.RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("1. Other Device (platform-id-one)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2. Device Two (platform-id-two)", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Select a device", output.ToString(), StringComparison.Ordinal);
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
        var command = new DeviceGetCommand(output, error);

        var exitCode = await command.Parse(["get", "--peripheral-id", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("macOS", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task DeviceGetRoutesToItsReadOperation()
    {
        string? selectedId = null;
        var command = new CliRootCommand(
            new DeviceCommand(
                new DeviceGetCommand((peripheralId, _) =>
                {
                    selectedId = peripheralId;
                    return Task.FromResult(0);
                }),
                new DeviceListCommand(() => null,
                    _ => throw new InvalidOperationException("Get must not scan."),
                    TextWriter.Null, TextWriter.Null)),
            new PromptCommand((_, _, _, _) =>
                throw new InvalidOperationException("Get must not open the prompt.")));

        var exitCode = await command.Parse(["device", "get", "--peripheral-id", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("selected-device", selectedId);
    }

    [Fact]
    public async Task ForegroundPromptParsesValidTupleButRefusesMotion()
    {
        using var input = new StringReader("set --speed 25 --stroke 30 --depth 60 --pattern SimpleStroke\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error, () => null);
        var exitCode = await handler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Motion unavailable", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1", "20", "50", "SimpleStroke")]
    [InlineData("101", "20", "50", "SimpleStroke")]
    [InlineData("20", "101", "50", "SimpleStroke")]
    [InlineData("20", "60", "50", "SimpleStroke")]
    [InlineData("20", "20", "50", "Unknown")]
    [InlineData("20", "20", "101", "SimpleStroke")]
    public async Task ForegroundPromptRejectsUnsafeOrUnsupportedValues(
        string speed, string stroke, string depth, string pattern)
    {
        using var input = new StringReader(
            $"set --speed {speed} --stroke {stroke} --depth {depth} --pattern {pattern}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error, () => null);
        await handler.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(error.ToString());
        Assert.DoesNotContain("Motion unavailable", error.ToString(), StringComparison.Ordinal);
    }

    private static CliRootCommand CreateRoot(PromptCommand prompt) => new(
        new DeviceCommand(
            new DeviceGetCommand((_, _) => Task.FromResult(0)),
            new DeviceListCommand(
                () => new MemoryStream(),
                _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([]),
                TextWriter.Null,
                TextWriter.Null)),
        prompt);

    private static string CreateTemporaryFile()
    {
        var path = Path.GetTempFileName();
        return Path.GetFullPath(path);
    }
}
