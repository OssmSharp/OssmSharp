using OssmSharp.Cli.Models;
using OssmSharp.Cli.Simulator;
using OssmSharp.Cli.Tests.Helpers;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests;

public sealed class SimulatorPromptTests
{
    [Fact]
    public async Task SimulatorSetAndStopSettleAtZeroBeforeClosingDevice()
    {
        using var input = new StringReader(
            "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();

        var session = CreateSession(input, output, error, _ => device);
        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("write settled", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(device.MotionCount >= 3);
        Assert.Equal(0, device.LastMotion?.Speed);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task MovingEnvelopeChangeRequiresStopBeforeAnotherWrite()
    {
        using var input = new StringReader(
            "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke\n"
            + "set --speed 12 --stroke 30 --depth 60 --pattern SimpleStroke\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();

        var session = CreateSession(input, output, error, _ => device);
        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Stop and confirm zero", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
    }

    [Fact]
    public async Task InvalidMotionInputDoesNotDispatchMovingSettings()
    {
        using var input = new StringReader(
            "set --speed 20 --stroke 0 --depth 50 --pattern SimpleStroke\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();

        var session = CreateSession(input, output, error, _ => device);
        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("positive stroke", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.MotionCount);
        Assert.Equal(0, device.LastMotion?.Speed);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(100, 100, 100)]
    public async Task BoundaryMotionSettingsAreSentBeforeExitStop(int speed, int stroke, int depth)
    {
        using var input = new StringReader(
            $"set --speed {speed} --stroke {stroke} --depth {depth} --pattern SimpleStroke\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        var reported = new List<MotionSettings>();
        device.DeviceStateChanged += change =>
        {
            if (change.IsT0)
                reported.Add(change.AsT0.MotionSettings);
        };

        var session = CreateSession(input, output, error, _ => device);
        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(reported, settings =>
            settings.Speed == speed && settings.Stroke == stroke && settings.Depth == depth);
        Assert.Equal(0, device.LastMotion?.Speed);
    }

    [Fact]
    public async Task SimulatorAttachNeverStartsOrTerminatesAnExternalChild()
    {
        using var input = new StringReader("help\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();
        var processCreated = false;
        var endpoint = string.Empty;
        var session = CreateSession(
            input,
            output,
            error,
            selected =>
            {
                endpoint = selected;
                return device;
            },
            createSimulator: _ =>
            {
                processCreated = true;
                throw new InvalidOperationException("Attached session started a child.");
            });

        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(Attach().Value, endpoint);
        Assert.False(processCreated);
        Assert.True(device.Disposed);
        Assert.Contains("Reported", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatorEofStillStopsAndDisposesDevice()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();

        var session = CreateSession(input, output, error, _ => device);
        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(device.MotionCount >= 1);
        Assert.Equal(0, device.LastMotion?.Speed);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task SimulatorWaitsForInitialFeedbackBeforeReadingCommands()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        var session = CreateSession(input, output, error, _ => device);

        var running = session.RunAsync(Attach(), TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted);

        device.Report(StoppedReport());
        await Assert.ThrowsAsync<NotSupportedException>(() => running);
        Assert.Equal(1, device.Writes);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task SimulatorAcknowledgeRemainsExplicitButUnavailableWithoutRuntimeRevision()
    {
        using var input = new StringReader("help\nacknowledge\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var device = new SimulatorHostTestDevice();

        var session = CreateSession(input, output, error, _ => device);
        var exitCode = await session.RunAsync(Attach(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("acknowledge", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Acknowledgement unavailable", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, device.LastMotion?.Speed);
    }

    [Fact]
    public async Task ChildFactoryFailureDoesNotConstructDevice()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var childFactoryCalled = false;
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Device started after child failure."),
            createSimulator: _ =>
            {
                childFactoryCalled = true;
                throw new IOException("Simulator startup failed.");
            });

        var exitCode = await session.RunAsync(
            PromptSelection.Parse(null, Environment.ProcessPath!),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.True(childFactoryCalled);
        Assert.Contains("Simulator startup failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BluetoothIdWinsOverAnotherDevicesMatchingName()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice();
        var selectedId = string.Empty;
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            id =>
            {
                selectedId = id;
                return device;
            },
            _ =>
            {
                return Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                    [("First", "id-one"), ("id-one", "id-two")]);
            });

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("ID-ONE", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("id-one", selectedId);
        Assert.Equal(0, device.Writes);
        Assert.True(device.Disposed);
        Assert.Contains("motion control unavailable", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BluetoothUniqueExactNameFreezesIdForReadOnlySession()
    {
        using var input = new StringReader("set --speed 25 --stroke 20 --depth 50 --pattern SimpleStroke\nstop\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice();
        var selectedId = string.Empty;
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            id =>
            {
                selectedId = id;
                return device;
            },
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Other", "id-one"), ("Named device", "id-two")]));

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("Named device", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("id-two", selectedId);
        Assert.Equal(0, device.Writes);
        Assert.True(device.Disposed);
        Assert.Contains("Motion unavailable", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Stop unavailable", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "not found")]
    [InlineData("named device", "not found")]
    [InlineData("Shared name", "ambiguous")]
    public async Task BluetoothUnknownOrAmbiguousNameNeverConnects(string selection, string diagnostic)
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var deviceCreated = false;
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            _ =>
            {
                deviceCreated = true;
                throw new InvalidOperationException("Invalid selection connected.");
            },
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>(
                [("Shared name", "id-one"), ("Shared name", "id-two"), ("Named device", "id-three")]));

        var exitCode = await session.RunAsync(
            PromptSelection.Parse(selection, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(deviceCreated);
        Assert.Contains(diagnostic, error.ToString(), StringComparison.OrdinalIgnoreCase);
        if (selection == "Shared name")
        {
            Assert.Contains("id-one", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("id-two", error.ToString(), StringComparison.Ordinal);
        }

    }

    [Fact]
    public void BluetoothSelectionDiagnosticReplacesUnicodeFormatting()
    {
        var missing = Assert.Throws<ArgumentException>(() =>
            PromptSession.ResolveBluetoothId("Unknown\u202Ename", []));
        var ambiguous = Assert.Throws<ArgumentException>(() =>
            PromptSession.ResolveBluetoothId(
                "Shared\u202Ename",
                [("Shared\u202Ename", "id\u202E-one"), ("Shared\u202Ename", "id-two")]));

        Assert.Contains("'Unknown name' not found", missing.Message, StringComparison.Ordinal);
        Assert.Contains("Shared name (id -one), Shared name (id-two)", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BluetoothConnectionFailureReplacesUnicodeFormatting()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            _ => throw new InvalidOperationException("Bad\u202Eidentity"),
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([("Device", "id-one")]));

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("id-one", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("Device connection failed: Bad identity", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BluetoothWithoutNativeProviderFailsRatherThanUsingSimulator()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth fell back to a simulator."));

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("selected-id", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("Stock BLE connection unavailable", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousDeviceDiagnosticsSanitizeDiscoveredControlCharacters()
    {
        var failure = Assert.Throws<ArgumentException>(() => PromptSession.ResolveBluetoothId(
            "Shared\nName",
            [("Shared\nName", "first\tid"), ("Shared\nName", "second\u001bid")]));

        Assert.Contains("first id", failure.Message, StringComparison.Ordinal);
        Assert.Contains("second id", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedPreliminaryStopStillDisposesDevice()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice
        {
            InitialReport = StoppedReport(),
        };
        var session = CreateSession(input, output, error, _ => device);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.RunAsync(Attach(), TestContext.Current.CancellationToken));

        Assert.Equal(1, device.Writes);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task BluetoothConnectionCleanupFailureIsReported()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice { FailDispose = true };
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            _ => device,
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([("Device", "device-id")]));

        var failure = await Assert.ThrowsAsync<IOException>(() => session.RunAsync(
            PromptSelection.Parse("device-id", null),
            TestContext.Current.CancellationToken));

        Assert.Contains("did not settle", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task FailedBluetoothConnectionDoesNotEnterPromptAndDisposesDevice()
    {
        using var input = new StringReader("status\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var device = new ReadOnlyTestDevice(connects: false);
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            _ => device,
            _ => Task.FromResult<IReadOnlyList<(string Name, string Id)>>([("Device", "device-id")]));

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("device-id", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("connection", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task BluetoothScannerFailureRemainsOrdinaryFailureWhenCancellationArrives()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            _ => throw new InvalidOperationException("Scanner failure started a device."),
            async _ =>
            {
                await cancellation.CancelAsync();
                throw new IOException("Scan failed before cancellation could stop it.");
            });

        var exitCode = await session.RunAsync(
            PromptSelection.Parse("device-id", null),
            cancellation.Token);

        Assert.Equal(1, exitCode);
        Assert.Contains("Scan failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BluetoothScanCancellationThrows()
    {
        using var input = new StringReader("quit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var session = CreateSession(
            input,
            output,
            error,
            _ => throw new InvalidOperationException("Bluetooth selected a simulator."),
            _ => throw new InvalidOperationException("Canceled scan started a device."),
            async _ =>
            {
                await cancellation.CancelAsync();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(
            PromptSelection.Parse("device-id", null),
            cancellation.Token));

    }

    private static PromptSelection Attach() =>
        PromptSelection.Parse(null, "tcp://127.0.0.1:19083");

    private static DeviceState StoppedReport() => new()
    {
        Timestamp = DateTime.UtcNow,
        MotionSettings = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle, 0, 20, 50, 50, 0, MotionPattern.SimpleStroke, 0),
        SessionId = Guid.NewGuid(),
    };

    private static PromptSession CreateSession(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, IOssmDevice> simulatedDevice,
        Func<string, IOssmDevice>? stockDevice = null,
        Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>>? scan = null,
        Func<string, SimulatorBackgroundService>? createSimulator = null) =>
        new(
            new PromptHandler(input, output, error),
            createSimulator ?? (_ => throw new InvalidOperationException("No child launch was expected.")),
            simulatedDevice,
            stockDevice,
            scan,
            error);
}
