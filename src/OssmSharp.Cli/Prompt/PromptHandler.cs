using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Simulator;

namespace OssmSharp.Cli;

/// <summary>
/// Defines the foreground prompt and owns its device session.
/// </summary>
internal sealed class PromptHandler : Command
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Func<IDisposable?> _acquireOwnership;
    private readonly Func<string, SimulatorBackgroundService>? _createSimulator;
    private readonly Func<string, SimulatedDevice>? _createSimulatedDevice;
    private readonly Func<string, string, IOssmDevice>? _createStockDevice;

    internal PromptHandler(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<IDisposable?> acquireOwnership)
        : base("prompt", "Open a foreground prompt for a selected device.")
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _acquireOwnership = acquireOwnership ?? throw new ArgumentNullException(nameof(acquireOwnership));
        ConfigureOptions();
    }

    internal PromptHandler(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<IDisposable?> acquireOwnership,
        Func<string, SimulatorBackgroundService> createSimulator,
        Func<string, SimulatedDevice> createSimulatedDevice,
        Func<string, string, IOssmDevice>? createStockDevice)
        : this(input, output, error, acquireOwnership)
    {
        _createSimulator = createSimulator ?? throw new ArgumentNullException(nameof(createSimulator));
        _createSimulatedDevice = createSimulatedDevice ?? throw new ArgumentNullException(nameof(createSimulatedDevice));
        _createStockDevice = createStockDevice;
    }

    private void ConfigureOptions()
    {
        var peripheralId = new Option<string>("--peripheral-id")
        {
            Description = "Platform-specific locator of the selected stock device.",
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && !string.IsNullOrWhiteSpace(result.Tokens[0].Value))
                    return result.Tokens[0].Value;

                result.AddError("Provide one nonempty peripheral ID.");
                return null;
            },
        };
        var wifiMac = new Option<string>("--wifi-mac")
        {
            Description = "Enrolled Wi-Fi MAC of the selected stock device (AA:BB:CC:DD:EE:FF).",
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && Regex.IsMatch(
                    result.Tokens[0].Value, @"\A(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}\z"))
                {
                    return result.Tokens[0].Value.ToUpperInvariant();
                }

                result.AddError("Provide an enrolled Wi-Fi MAC as six colon-separated hex octets.");
                return null;
            },
        };
        var simulator = new Option<string>("--simulator")
        {
            Description = "Absolute path to the ossm-sim-bridge executable.",
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && Path.IsPathFullyQualified(result.Tokens[0].Value) &&
                    File.Exists(result.Tokens[0].Value))
                {
                    return Path.GetFullPath(result.Tokens[0].Value);
                }

                result.AddError("Provide the absolute path to an existing simulator executable.");
                return null;
            },
        };
        Options.Add(peripheralId);
        Options.Add(wifiMac);
        Options.Add(simulator);
        Validators.Add(result =>
        {
            if (result.GetValue(simulator) is not null)
            {
                if (result.GetValue(peripheralId) is not null || result.GetValue(wifiMac) is not null)
                    result.AddError("--simulator cannot be combined with --peripheral-id or --wifi-mac.");
            }
            else if (result.GetValue(peripheralId) is null || result.GetValue(wifiMac) is null)
            {
                result.AddError("Stock control requires --peripheral-id and --wifi-mac.");
            }
        });
        SetAction((parse, cancellationToken) => RunAsync(
            parse.GetValue(peripheralId),
            parse.GetValue(wifiMac),
            parse.GetValue(simulator),
            cancellationToken));
    }

    internal async Task<int> RunAsync(
        string? peripheralId,
        string? wifiMac,
        string? simulatorPath,
        CancellationToken cancellationToken)
    {
        if (simulatorPath is null)
        {
            return _createStockDevice is null
                ? await RunWithOwnershipAsync(cancellationToken).ConfigureAwait(false)
                : await RunWithOwnershipAsync(
                    () => _createStockDevice(peripheralId!, wifiMac!), cancellationToken).ConfigureAwait(false);
        }

        var createSimulator = _createSimulator
            ?? throw new InvalidOperationException("Simulator process creation is not configured.");
        var createDevice = _createSimulatedDevice
            ?? throw new InvalidOperationException("Simulator device creation is not configured.");
        using var simulator = createSimulator(simulatorPath);
        return await RunWithOwnershipAsync(
            () => createDevice(simulator.Endpoint),
            async token =>
            {
                await simulator.StartAsync(token).ConfigureAwait(false);
                await simulator.WaitUntilReadyAsync(token).ConfigureAwait(false);
            },
            async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await simulator.StopAsync(timeout.Token).ConfigureAwait(false);
            },
            cancellationToken,
            simulatorControl: true).ConfigureAwait(false);
    }

    internal async Task<int> RunWithOwnershipAsync(CancellationToken cancellationToken)
    {
        var error = _error;
        var acquireOwnership = _acquireOwnership;

        IDisposable? ownership;
        try
        {
            ownership = acquireOwnership();
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        if (ownership is null)
        {
            await error.WriteLineAsync("Device is already owned by another process.").ConfigureAwait(false);
            return 1;
        }

        ownership.Dispose();
        await error.WriteLineAsync("Stock BLE connection unavailable: use a native BLE build with an enrolled device.")
            .ConfigureAwait(false);
        return 1;
    }

    internal Task<int> RunWithOwnershipAsync(
        Func<IOssmDevice> createDevice,
        CancellationToken cancellationToken) => RunWithOwnershipAsync(
            createDevice,
            _ => Task.CompletedTask, () => ValueTask.CompletedTask, cancellationToken);

    internal async Task<int> RunWithOwnershipAsync(
        Func<IOssmDevice> createDevice,
        Func<CancellationToken, Task> prepareDevice,
        Func<ValueTask> cleanupDevice,
        CancellationToken cancellationToken,
        bool simulatorControl = false)
    {
        ArgumentNullException.ThrowIfNull(createDevice);
        ArgumentNullException.ThrowIfNull(prepareDevice);
        ArgumentNullException.ThrowIfNull(cleanupDevice);
        var input = _input;
        var output = _output;
        var error = _error;

        IDisposable? lease;
        try
        {
            lease = _acquireOwnership();
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        if (lease is null)
        {
            await error.WriteLineAsync("Device is already owned by another process.").ConfigureAwait(false);
            return 1;
        }

        IOssmDevice? device = null;
        DeviceState? reported = null;
        MotionSettings? submitted = null;
        Exception? fault = null;
        void OnStateChanged(OneOf.OneOf<DeviceState, Exception> change)
        {
            if (change.IsT0)
                Volatile.Write(ref reported, change.AsT0);
            else
                Volatile.Write(ref fault, change.AsT1);
        }

        try
        {
            await prepareDevice(cancellationToken).ConfigureAwait(false);
            device = createDevice();
            if (device is not BackgroundService service)
            {
                throw new InvalidOperationException("The selected device cannot start a foreground connection.");
            }

            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnConnectionChanged(DeviceConnectionState state)
            {
                if (state == DeviceConnectionState.Connected)
                    connected.TrySetResult(true);
                else if (state == DeviceConnectionState.Disconnected)
                    connected.TrySetResult(false);
            }

            device.ConnectionStateChanged += OnConnectionChanged;
            device.DeviceStateChanged += OnStateChanged;
            try
            {
                await service.StartAsync(cancellationToken).ConfigureAwait(false);
                var completed = await Task.WhenAny(
                    connected.Task,
                    service.ExecuteTask ?? Task.CompletedTask,
                    Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed != connected.Task || !await connected.Task.ConfigureAwait(false) ||
                    device.ConnectionState != DeviceConnectionState.Connected)
                {
                    await error.WriteLineAsync("Device connection failed or enrolled identity did not match.")
                        .ConfigureAwait(false);
                    return 1;
                }

                return await RunAsync(
                    device,
                    () => Volatile.Read(ref reported),
                    () => Volatile.Read(ref fault),
                    cancellationToken, simulatorControl,
                    settings => submitted = settings).ConfigureAwait(false);
            }
            finally
            {
                device.ConnectionStateChanged -= OnConnectionChanged;
                device.DeviceStateChanged -= OnStateChanged;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Device connection failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            async ValueTask CleanupAsync()
            {
                try
                {
                    if (simulatorControl && device?.ConnectionState == DeviceConnectionState.Connected)
                    {
                        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await device.SetMotionAsync(
                            StoppedSettings(submitted ?? Volatile.Read(ref reported)?.MotionSettings),
                            stopTimeout.Token).WaitAsync(stopTimeout.Token).ConfigureAwait(false);
                    }

                    if (device is not null)
                        await device.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await cleanupDevice().ConfigureAwait(false);
                }
            }

            if (lease is OssmSharp.Device.Runtime.DeviceOwnershipLock machineLease)
                await machineLease.ReleaseAfterAsync(CleanupAsync).ConfigureAwait(false);
            else
            {
                await CleanupAsync().ConfigureAwait(false);
                lease.Dispose();
            }
        }
    }

    internal Task<int> RunAsync(CancellationToken cancellationToken) =>
        RunAsync(null, () => null, () => null, cancellationToken);

    private async Task<int> RunAsync(
        IOssmDevice? device,
        Func<DeviceState?> getReported,
        Func<Exception?> getFault,
        CancellationToken cancellationToken,
        bool simulatorControl = false,
        Action<MotionSettings>? recordSubmission = null)
    {
        var input = _input;
        var output = _output;
        var error = _error;

        var quit = false;
        var root = new RootCommand("Foreground device controls.");
        var help = new Command("help", "Show available commands.");
        help.SetAction(async (_, _) =>
            await output.WriteLineAsync(simulatorControl
                ? "Commands: help, status, set --speed N --stroke N --depth N --pattern SimpleStroke, stop, quit."
                : "Read-only connection. Commands: help, status, quit. Unavailable: set, stop, acknowledge."));
        var status = new Command("status", "Show device readiness.");
        status.SetAction(async (_, _) =>
        {
            if (device?.ConnectionState != DeviceConnectionState.Connected)
            {
                await output.WriteLineAsync("ConnectionState: Disconnected; device control unavailable.");
                return;
            }

            var information = device.Device;
            await output.WriteLineAsync($"Device: {information.Identity}; Profile: {information.ProfileId}; Firmware: {information.Version ?? "unknown"}; Reported patterns: {string.Join(", ", information.SupportedPatterns)}; ConnectionState: {device.ConnectionState}; {(simulatorControl ? "simulator control available" : "motion control unavailable")}.");
            var state = getReported();
            await output.WriteLineAsync(state is null
                ? "No reported feedback."
                : $"Reported at {state.Timestamp:O}: state {state.MotionSettings.State}, effective speed {state.MotionSettings.Speed}%, stroke {state.MotionSettings.Stroke}%, depth {state.MotionSettings.Depth}%, pattern {state.MotionSettings.Pattern}, position {state.MotionSettings.Position} mm; velocity {(state.VelocityMillimetresPerSecond is { } velocity ? $"{velocity} mm/s" : "unknown")}.");
            if (getFault() is { } errorState)
                await error.WriteLineAsync($"Device feedback error: {errorState.Message}");
        });
        var speed = Percentage("--speed", 0);
        var stroke = Percentage("--stroke", 0);
        var depth = Percentage("--depth", 0);
        var pattern = new Option<string>("--pattern") { Required = true };
        var set = new Command("set", "Set motion only when runtime control is available.");
        set.Options.Add(speed);
        set.Options.Add(stroke);
        set.Options.Add(depth);
        set.Options.Add(pattern);
        set.Validators.Add(result => ValidateSet(result, speed, stroke, depth, pattern));
        set.SetAction((parse, _) => SetSimulatorMotionAsync(
            device, simulatorControl, parse.GetValue(speed), parse.GetValue(stroke),
            parse.GetValue(depth), parse.GetValue(pattern)!, getReported, getFault,
            recordSubmission, cancellationToken));
        var stop = new Command("stop", "Request a serialized zero-speed Stop.");
        stop.SetAction(async (_, _) =>
        {
            if (!simulatorControl || device?.ConnectionState != DeviceConnectionState.Connected)
            {
                await error.WriteLineAsync("Stop unavailable: no connected simulator session.").ConfigureAwait(false);
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await device.SetMotionAsync(StoppedSettings(getReported()?.MotionSettings), timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            await output.WriteLineAsync("Stop write settled; reported state is separate.").ConfigureAwait(false);
        });
        var acknowledge = new Command("acknowledge", "Acknowledge a detected physical interruption while paused.");
        acknowledge.SetAction(async (_, _) => await error.WriteLineAsync("Acknowledgement unavailable: no runtime interruption revision."));
        var exit = new Command("quit", "Exit the foreground prompt.");
        exit.SetAction((_, _) =>
        {
            quit = true;
            return Task.FromResult(0);
        });
        root.Subcommands.Add(help);
        root.Subcommands.Add(status);
        root.Subcommands.Add(set);
        root.Subcommands.Add(stop);
        root.Subcommands.Add(acknowledge);
        root.Subcommands.Add(exit);

        while (!quit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = root.Parse(line);
            if (parsed.Errors.Count != 0)
            {
                foreach (var issue in parsed.Errors)
                {
                    await error.WriteLineAsync(issue.Message).ConfigureAwait(false);
                }

                continue;
            }

            await parsed.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return device is null || device.ConnectionState == DeviceConnectionState.Connected ? 0 : 1;
    }

    private Option<int> Percentage(string name, int minimum) => new(name)
    {
        Required = true,
        CustomParser = result =>
        {
            if (result.Tokens.Count == 1 &&
                int.TryParse(result.Tokens[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
                value >= minimum && value <= 100)
            {
                return value;
            }

            result.AddError($"Provide an integer from {minimum} through 100 for {name} (received {string.Join(' ', result.Tokens.Select(token => token.Value))}).");
            return default;
        },
    };

    private MotionSettings StoppedSettings(MotionSettings? current) =>
        (current ?? new MotionSettings(
            MotionDeviceState.StrokeEngineIdle, 0, 20, 50, 50, 0, MotionPattern.SimpleStroke, 0)) with
        { Speed = 0 };

    private void ValidateSet(
        CommandResult result,
        Option<int> speed,
        Option<int> stroke,
        Option<int> depth,
        Option<string> pattern)
    {
        var strokeValue = result.GetValue(stroke);
        if (strokeValue > result.GetValue(depth))
            result.AddError("Stroke must not exceed depth.");

        if (result.GetValue(speed) > 0 && strokeValue == 0)
            result.AddError("Nonzero speed requires a positive stroke.");

        var name = result.GetValue(pattern);
        if (name is not null &&
            (!Enum.TryParse<MotionPattern>(name, ignoreCase: true, out var selected) ||
             !Enum.IsDefined(selected)))
        {
            result.AddError("Provide a supported motion pattern name.");
        }
    }

    private async Task SetSimulatorMotionAsync(
        IOssmDevice? device,
        bool simulatorControl,
        int speed,
        int stroke,
        int depth,
        string pattern,
        Func<DeviceState?> getReported,
        Func<Exception?> getFault,
        Action<MotionSettings>? recordSubmission,
        CancellationToken cancellationToken)
    {
        var output = _output;
        var error = _error;
        if (!simulatorControl || device?.ConnectionState != DeviceConnectionState.Connected)
        {
            await error.WriteLineAsync("Motion unavailable: no connected simulator session.").ConfigureAwait(false);
            return;
        }

        if (getFault() is not null || getReported() is not { } report ||
            report.MotionSettings.State != MotionDeviceState.StrokeEngineIdle ||
            TimeProvider.System.GetUtcNow().UtcDateTime - report.Timestamp > TimeSpan.FromSeconds(5))
        {
            await error.WriteLineAsync("Motion unavailable: recent stopped-ready feedback is required.").ConfigureAwait(false);
            return;
        }

        var selected = Enum.Parse<MotionPattern>(pattern, ignoreCase: true);
        if (selected != MotionPattern.SimpleStroke ||
            !device.Device.SupportedPatterns.Contains(selected))
        {
            await error.WriteLineAsync("The simulator supports SimpleStroke only.").ConfigureAwait(false);
            return;
        }

        var current = report.MotionSettings;
        var desired = current with
        {
            Speed = speed,
            Stroke = stroke,
            Depth = depth,
            Sensation = 50,
            Buffer = 0,
            Pattern = selected,
        };
        if (current.Speed != 0 &&
            (current.Stroke != desired.Stroke || current.Depth != desired.Depth))
        {
            await error.WriteLineAsync("Stop and confirm zero before changing stroke or depth.").ConfigureAwait(false);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        if (current.Speed == 0)
        {
            var smallerStroke = current with { Stroke = Math.Min(current.Stroke, desired.Stroke) };
            if (smallerStroke.Stroke != current.Stroke)
                await WriteStoppedAsync(device, smallerStroke, getReported, timeout.Token).ConfigureAwait(false);

            var newDepth = smallerStroke with { Depth = desired.Depth };
            if (newDepth.Depth != smallerStroke.Depth)
                await WriteStoppedAsync(device, newDepth, getReported, timeout.Token).ConfigureAwait(false);

            var newStroke = newDepth with { Stroke = desired.Stroke };
            if (newStroke.Stroke != newDepth.Stroke)
                await WriteStoppedAsync(device, newStroke, getReported, timeout.Token).ConfigureAwait(false);
        }

        await device.SetMotionAsync(desired, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        recordSubmission?.Invoke(desired);
        await output.WriteLineAsync("Simulator write settled; check status for reported state.").ConfigureAwait(false);
    }

    private async Task WriteStoppedAsync(
        IOssmDevice device,
        MotionSettings settings,
        Func<DeviceState?> getReported,
        CancellationToken cancellationToken)
    {
        await device.SetMotionAsync(settings, cancellationToken).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (getReported()?.MotionSettings is not { Speed: 0 } observed ||
            observed.Stroke != settings.Stroke || observed.Depth != settings.Depth)
        {
            throw new InvalidDataException("The simulator did not report the stopped intermediate settings.");
        }
    }
}
