using OssmSharp.Cli.Commands;
using OssmSharp.Cli.Models;
using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

/// <summary>
/// Verifies device CLI parsing, discovery and foreground control.
/// </summary>
public sealed class DeviceCommandTests
{
    [Fact]
    public void OnlyDevicesAndPromptCommandsAreExposed()
    {
        var command = CreateRoot(new PromptCommand((_, _) => Task.FromResult(0)));

        Assert.Equal(["devices", "prompt"], command.Subcommands.Select(child => child.Name));
        foreach (var name in new[] { "device", "simulator", "start-simulator", "status", "identify", "scan", "move", "stop" })
        {
            Assert.NotEmpty(command.Parse([name]).Errors);
        }
    }

    [Fact]
    public void SimulatorPromptRequiresAnExistingExecutableOrLocalEndpoint()
    {
        var command = CreateRoot(new PromptCommand((_, _) => Task.FromResult(0)));

        var missing = command.Parse(["prompt"]);
        var nonlocal = command.Parse(["prompt", "--simulator", "tcp://192.168.1.2:6000"]);
        var malformed = command.Parse(["prompt", "--simulator", "tcp://127.0.0.1:0"]);
        var absent = command.Parse([
            "prompt", "--simulator",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        ]);

        Assert.NotEmpty(missing.Errors);
        Assert.NotEmpty(nonlocal.Errors);
        Assert.NotEmpty(malformed.Errors);
        Assert.NotEmpty(absent.Errors);
    }

    [Fact]
    public async Task InvalidPromptDoesNotRunTheDevice()
    {
        var invoked = false;
        var command = CreateRoot(new PromptCommand((_, _) =>
        {
            invoked = true;
            return Task.FromResult(0);
        }));

        var parse = command.Parse(["prompt", "--bluetooth", " ", "--simulator", "tcp://127.0.0.1:6123"]);
        await parse.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(parse.Errors);
        Assert.False(invoked);
    }

    [Fact]
    public async Task PromptForwardsTheSimulatorPath()
    {
        var path = CreateTemporaryExecutableFile();
        PromptSelection? selected = null;
        try
        {
            var command = CreateRoot(new PromptCommand((selection, _) =>
            {
                selected = selection;
                return Task.FromResult(0);
            }));

            var exitCode = await command.Parse(["prompt", "--simulator", path])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(PromptMode.SimulatorLaunch, selected?.Mode);
            Assert.Equal(Path.GetFullPath(path), selected?.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PromptForwardsTheLocalSimulatorEndpoint()
    {
        PromptSelection? selected = null;
        var command = CreateRoot(new PromptCommand((selection, _) =>
        {
            selected = selection;
            return Task.FromResult(0);
        }));

        var exitCode = await command.Parse(["prompt", "--simulator", "tcp://127.0.0.1:6123"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(PromptMode.SimulatorAttach, selected?.Mode);
        Assert.Equal("tcp://127.0.0.1:6123", selected?.Value);
    }

    [Theory]
    [InlineData("tcp://localhost:6123")]
    [InlineData("tcp://127.0.0.1:0")]
    [InlineData("tcp://127.0.0.1:65536")]
    [InlineData("tcp://127.0.0.1:6123/other")]
    [InlineData("tcp://user@127.0.0.1:6123")]
    [InlineData("http://127.0.0.1:6123")]
    [InlineData("tcp://127.0.0.1:6123?token=x")]
    public void PromptRejectsInvalidSimulatorEndpointsWithoutInvoking(string endpoint)
    {
        var invoked = false;
        var command = new PromptCommand((_, _) =>
        {
            invoked = true;
            return Task.FromResult(0);
        });

        var parse = command.Parse(["prompt", "--simulator", endpoint]);

        Assert.NotEmpty(parse.Errors);
        Assert.False(invoked);
    }

    [Fact]
    public async Task PromptResolvesRelativeExecutablePathsContainingSpaces()
    {
        var path = CreateTemporaryExecutableFile();
        try
        {
            PromptSelection? selected = null;
            var command = new PromptCommand((selection, _) =>
            {
                selected = selection;
                return Task.FromResult(0);
            });
            var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);

            var exitCode = await command.Parse(["prompt", "--simulator", relative])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(PromptMode.SimulatorLaunch, selected?.Mode);
            Assert.Equal(path, selected?.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PromptRejectsAnExistingNonExecutableFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var command = new PromptCommand((_, _) =>
                throw new InvalidOperationException("A nonexecutable file must not start the session."));

            var parse = command.Parse(["prompt", "--simulator", path]);

            Assert.NotEmpty(parse.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WindowsDrivePathIsNotInterpretedAsAnEndpointOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = $"C:\\missing-{Guid.NewGuid():N}\\bridge.exe";

        var error = Assert.Throws<ArgumentException>(() => PromptSelection.Parse(null, path));

        Assert.Contains("executable", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRequiresAPositionalIdWithoutOpeningASelector()
    {
        string? selectedId = null;
        var command = new DeviceGetCommand((peripheralId, _) =>
        {
            selectedId = peripheralId;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["get"]).Errors);
        Assert.NotEmpty(command.Parse(["get", "--peripheral-id", "selected-device"]).Errors);
        Assert.NotEmpty(command.Parse(["get", " "]).Errors);
        Assert.Null(selectedId);

        var exitCode = await command.Parse(["get", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("selected-device", selectedId);
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

        var exitCode = await command.Parse(["get", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("selected-device", selectedId);
    }

    [Fact]
    public async Task GetReadsOnlyTheDiscoveredPlatformId()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string? readId = null;
        var reader = new StockIdentityReader(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Other", "id-other"), ("Selected\nName", "id-selected")]),
            (id, _) =>
            {
                readId = id;
                return Task.FromResult("Firmware: 1.0.34");
            });
        var command = new DeviceGetCommand(reader.ReadAsync, output, error);

        var exitCode = await command.Parse(["get", "id-selected"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("id-selected", readId);
        Assert.Contains("Peripheral ID: id-selected", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Name: Selected Name", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Firmware: 1.0.34", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task GetDoesNotConnectForAnUnknownPlatformId()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var reader = new StockIdentityReader(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([("Device", "id-present")]),
            (_, _) => throw new InvalidOperationException("Unknown ID must not be read."));
        var command = new DeviceGetCommand(reader.ReadAsync, output, error);

        var exitCode = await command.Parse(["get", "id-absent"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("id-absent", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOmitsAnUnavailableDeviceName()
    {
        var reader = new StockIdentityReader(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([("", "id-unknown-name")]),
            (_, _) => Task.FromResult("Firmware: 1.0.34"));

        var details = await reader.ReadAsync("id-unknown-name", TestContext.Current.CancellationToken);

        Assert.Contains("Peripheral ID: id-unknown-name", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Name:", details, StringComparison.Ordinal);
        Assert.Contains("Firmware: 1.0.34", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDoesNotAcceptADeviceNameAsThePositionalId()
    {
        var read = false;
        var reader = new StockIdentityReader(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([("Matching name", "id-1")]),
            (_, _) =>
            {
                read = true;
                return Task.FromResult("Firmware: 1.0.34");
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync("Matching name", TestContext.Current.CancellationToken));

        Assert.False(read);
    }

    [Fact]
    public async Task GetReportsWhenDisconnectDoesNotSettle()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DeviceGetCommand(
            (_, _) => throw new UnsettledDeviceCleanupException(
                "Stock BLE disconnection did not settle.", new TimeoutException()),
            output, error);

        var exitCode = await command.Parse(["get", "id-selected"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("did not settle", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanNeedsNoEnrolledIdentity()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DeviceListCommand(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([]), output, error);

        var exitCode = await command.Parse(["list"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("No stock devices found", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ScanPrintsDiscoveredLocators()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var command = new DeviceListCommand(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Found device", "platform-id")]),
            output, error);

        var exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Found device (platform-id)", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DiscoveryAndIdentityReplaceUnicodeFormattingInDisplayedDeviceText()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        const string id = "id\u202E-selected";
        const string name = "Pa\u202Eir\u2028ing";
        var candidates = new (string Name, string Id)[] { (name, id) };
        var list = new DeviceListCommand(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(candidates),
            output, error);
        var reader = new StockIdentityReader(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(candidates),
            (_, _) => Task.FromResult("Firmware: 1.0.34"));

        var exitCode = await list.RunAsync(TestContext.Current.CancellationToken);
        var details = await reader.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal($"1. Pa ir ing (id -selected){Environment.NewLine}", output.ToString());
        Assert.Equal($"Peripheral ID: id -selected{Environment.NewLine}"
            + $"Name: Pa ir ing{Environment.NewLine}Firmware: 1.0.34", details);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task UnsupportedBuildReportsUnavailableDiscovery()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DeviceListCommand(
            _ => throw new PlatformNotSupportedException("A native BLE build is required."),
            output, error);

        var exitCode = await command.Parse(["list"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("native BLE build", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task PromptRequiresBluetoothOrSimulatorWithoutEnrolledWifiMac()
    {
        PromptSelection? selected = null;
        var command = new PromptCommand((selection, _) =>
        {
            selected = selection;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["prompt"]).Errors);
        Assert.NotEmpty(command.Parse(["prompt", "--bluetooth", " "]).Errors);
        Assert.NotEmpty(command.Parse(["prompt", "--peripheral-id", "device-1"]).Errors);
        Assert.NotEmpty(command.Parse(["prompt", "--wifi-mac", "AA:BB:CC:DD:EE:FF"]).Errors);
        Assert.NotEmpty(command.Parse([
            "prompt", "--bluetooth", "device-1", "--simulator", "tcp://127.0.0.1:6123",
        ]).Errors);

        var exitCode = await command.Parse(["prompt", "--bluetooth", "device-1"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(PromptMode.Bluetooth, selected?.Mode);
        Assert.Equal("device-1", selected?.Value);
    }

    [Theory]
    [InlineData("--endpoint")]
    [InlineData("--path")]
    [InlineData("--simulator-path")]
    [InlineData("--peripheral-id")]
    [InlineData("--wifi-mac")]
    public void PromptRejectsObsoleteStartupOptions(string option)
    {
        var command = new PromptCommand((_, _) =>
            throw new InvalidOperationException("Invalid input must not invoke the session."));

        var parse = command.Parse(["prompt", option, "value"]);

        Assert.NotEmpty(parse.Errors);
    }

    [Fact]
    public void PromptRejectsAnUnexpectedPositionalArgument()
    {
        var command = new PromptCommand((_, _) =>
            throw new InvalidOperationException("Unexpected input must not invoke the session."));

        var parse = command.Parse(["prompt", "--bluetooth", "id-1", "extra"]);

        Assert.NotEmpty(parse.Errors);
    }

    [Fact]
    public async Task ForegroundPromptRejectsMotionAndKeepsReadingUntilQuit()
    {
        using var input = new StringReader("help\nset --speed 25 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nacknowledge\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error);
        var exitCode = await handler.RunAsync(
            null, () => null, () => null, TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error);
        var exitCode = await handler.RunAsync(
            null, () => null, () => null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("101", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptCleansUpDeviceAfterQuit()
    {
        using var input = new StringReader("help\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice();

        var handler = new PromptHandler(input, output, error);
        var session = CreateReadOnlySession(handler, _ => device, error);
        var exitCode = await session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(device.Disposed);
        Assert.Contains("status", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForegroundPromptCannotSucceedWithoutAConnectedDevice()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handler = new PromptHandler(input, output, error);
        var session = CreateReadOnlySession(handler, null, error);
        var exitCode = await session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForegroundPromptReportsVerifiedConnectionAndOnlyReportedFeedback()
    {
        using var input = new StringReader("status\nstop\nacknowledge\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice();

        var handler = new PromptHandler(input, output, error);
        var session = CreateReadOnlySession(handler, _ => device, error);
        var command = new PromptCommand(session);
        var exitCode = await command.Parse(["prompt", "--bluetooth", "enrolled-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("enrolled-device", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Connected", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1.0.34", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("SimpleStroke", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("No reported feedback", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, device.Writes);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task ForegroundPromptReplacesUnicodeFormattingInDeviceStatus()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice(information: new DeviceInformation(
            "id\u202E-one", "sto\u2028ck", "1.0\u202E34", [MotionPattern.SimpleStroke]));

        var session = CreateReadOnlySession(
            new PromptHandler(input, output, error), _ => device, error);

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Device: id -one; Profile: sto ck; Firmware: 1.0 34", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ForegroundPromptRejectsFailedConnectionAndCleansUpDevice()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice(connects: false);

        var handler = new PromptHandler(input, output, error);
        var session = CreateReadOnlySession(handler, _ => device, error);
        var exitCode = await session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("connection", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task ForegroundPromptReportsDeviceCleanupFailure()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice { FailDispose = true };

        var handler = new PromptHandler(input, output, error);
        var session = CreateReadOnlySession(handler, _ => device, error);
        var failure = await Assert.ThrowsAsync<IOException>(() => session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken));

        Assert.Contains("did not settle", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task ForegroundPromptRetainsTheFirstValidatedReportDuringConnection()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice
        {
            InitialReport = new DeviceState
            {
                Timestamp = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc),
                MotionSettings = new MotionSettings(MotionDeviceState.StrokeEngineIdle, 25, 20, 50, 60, 0, MotionPattern.SimpleStroke, 10),
                SessionId = Guid.Empty,
            },
        };

        var handler = new PromptHandler(input, output, error);
        var session = CreateReadOnlySession(handler, _ => device, error);
        var exitCode = await session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("2026-09-26T12:00:00", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("effective speed 25%", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("velocity unknown", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task ForegroundPromptCleansUpOnEofAndCancellation()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var eofInput = new StringReader(string.Empty);
        using var eofDevice = new ReadOnlyTestDevice();

        var eofHandler = new PromptHandler(eofInput, output, error);
        var eofSession = CreateReadOnlySession(eofHandler, _ => eofDevice, error);
        Assert.Equal(0, await eofSession.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken));
        Assert.True(eofDevice.Disposed);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var canceledInput = new StringReader("status\n");
        using var canceledDevice = new ReadOnlyTestDevice();

        var canceledHandler = new PromptHandler(canceledInput, output, error);
        var canceledSession = CreateReadOnlySession(canceledHandler, _ => canceledDevice, error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledSession.RunAsync(
            PromptSelection.Parse("enrolled-device", null), cancellation.Token));
        Assert.True(canceledDevice.Disposed);
    }

    [Fact]
    public async Task ForegroundPromptDetectsConnectionExitWithoutAnotherInputLine()
    {
        using var input = new PendingLineReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice
        {
            ExitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        var session = CreateReadOnlySession(
            new PromptHandler(input, output, error), _ => device, error);
        var run = session.RunAsync(
            PromptSelection.Parse("enrolled-device", null), TestContext.Current.CancellationToken);

        await input.Reading.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        device.ExitGate.TrySetResult();
        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("disconnected", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task ForegroundPromptCancelsPendingInputAndCleansUp()
    {
        using var input = new PendingLineReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        using var device = new ReadOnlyTestDevice();
        var session = CreateReadOnlySession(
            new PromptHandler(input, output, error), _ => device, error);
        var invocation = Program.InvokeAsync(
            CreateRoot(new PromptCommand(session)),
            ["prompt", "--bluetooth", "enrolled-device"],
            cancellation.Token);

        await input.Reading.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var exitCode = await invocation.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(130, exitCode);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task InterruptedForegroundCommandReturnsCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await Program.InvokeAsync(
            CreateRoot(new PromptCommand((_, token) => Task.FromCanceled<int>(token))),
            ["prompt", "--bluetooth", "device-1"],
            cancellation.Token);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task CanceledDeviceListReturnsCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new CliRootCommand(
            new DeviceCommand(
                new DeviceGetCommand((_, _) => Task.FromResult(0)),
                new DeviceListCommand(
                    async _ =>
                    {
                        await cancellation.CancelAsync();
                        return await Task.FromCanceled<IReadOnlyList<(string Name, string Id)>>(cancellation.Token);
                    },
                    TextWriter.Null,
                    TextWriter.Null)),
            new PromptCommand((_, _) => Task.FromResult(0)));

        var exitCode = await Program.InvokeAsync(root, ["devices", "list"], cancellation.Token);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task CanceledDeviceGetReturnsCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new CliRootCommand(
            new DeviceCommand(
                new DeviceGetCommand(
                    async (_, _) =>
                    {
                        await cancellation.CancelAsync();
                        return await Task.FromCanceled<string>(cancellation.Token);
                    },
                    TextWriter.Null,
                    TextWriter.Null),
                new DeviceListCommand(
                    _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([]),
                    TextWriter.Null,
                    TextWriter.Null)),
            new PromptCommand((_, _) => Task.FromResult(0)));

        var exitCode = await Program.InvokeAsync(root, ["devices", "get", "selected-id"], cancellation.Token);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task OrdinarySessionFailureIsNotOverwrittenByCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var command = CreateRoot(new PromptCommand(async (_, _) =>
        {
            await cancellation.CancelAsync();
            return 1;
        }));

        var exitCode = await Program.InvokeAsync(
            command, ["prompt", "--bluetooth", "selected-device"], cancellation.Token);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task SuccessfulPromptCanceledDuringInputReturnsCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        var command = new PromptCommand(async (_, _) =>
        {
            await cancellation.CancelAsync();
            return 0;
        });

        var exitCode = await command.Parse(["prompt", "--bluetooth", "selected-device"])
            .InvokeAsync(cancellationToken: cancellation.Token);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task ScanPrintsPlatformIdsWithoutPrompting()
    {
        using var output = new StringWriter();
        var command = new DeviceListCommand(
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Other\nDevice", "platform-id-one"), ("Device Two", "platform-id-two")]),
            output, TextWriter.Null);

        await command.RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("1. Other Device (platform-id-one)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2. Device Two (platform-id-two)", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Select a device", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifyDoesNotUseTheSimulatorHost()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DeviceGetCommand(output, error);

        var exitCode = await command.Parse(["get", "selected-device"])
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
                new DeviceListCommand(
                    _ => throw new InvalidOperationException("Get must not scan."),
                    TextWriter.Null, TextWriter.Null)),
            new PromptCommand((_, _) =>
                throw new InvalidOperationException("Get must not open the prompt.")));

        var exitCode = await command.Parse(["devices", "get", "selected-device"])
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

        var handler = new PromptHandler(input, output, error);
        var exitCode = await handler.RunAsync(
            null, () => null, () => null, TestContext.Current.CancellationToken);

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

        var handler = new PromptHandler(input, output, error);
        await handler.RunAsync(
            null, () => null, () => null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(error.ToString());
        Assert.DoesNotContain("Motion unavailable", error.ToString(), StringComparison.Ordinal);
    }

    private static CliRootCommand CreateRoot(PromptCommand prompt) => new(
        new DeviceCommand(
            new DeviceGetCommand((_, _) => Task.FromResult(0)),
            new DeviceListCommand(
                _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([]),
                TextWriter.Null,
                TextWriter.Null)),
        prompt);

    private static PromptSession CreateReadOnlySession(
        PromptHandler prompt,
        Func<string, IOssmDevice>? createStockDevice,
        TextWriter error) => new(
            prompt,
            _ => throw new InvalidOperationException("A stock session must not launch the simulator."),
            _ => throw new InvalidOperationException("A stock session must not attach to the simulator."),
            createStockDevice,
            createStockDevice is null
                ? null
                : _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                    [("enrolled-device", "enrolled-device")]),
            error);

    private static string CreateTemporaryExecutableFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N} executable{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
        File.WriteAllText(path, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class PendingLineReader : TextReader
    {
        private readonly TaskCompletionSource<string?> _line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reading => _reading.Task;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _reading.TrySetResult();
            return new ValueTask<string?>(_line.Task.WaitAsync(cancellationToken));
        }
    }
}
