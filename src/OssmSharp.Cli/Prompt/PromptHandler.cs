using System.CommandLine.Parsing;
using System.Globalization;
using OssmSharp.Device.Primitives;

namespace OssmSharp.Cli;

/// <summary>
/// Defines the foreground prompt for a selected, externally owned device session.
/// </summary>
internal sealed class PromptHandler
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    internal PromptHandler(TextReader input, TextWriter output, TextWriter error)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    internal static MotionSettings StoppedSettings(MotionSettings? current) =>
        (current ?? new MotionSettings(
            MotionDeviceState.StrokeEngineIdle, 0, 20, 50, 50, 0, MotionPattern.SimpleStroke, 0)) with
        { Speed = 0 };

    internal async Task<int> RunAsync(
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
                ? "Commands: help, status, set --speed N --stroke N --depth N --pattern SimpleStroke, stop, acknowledge, quit. Acknowledge requires a runtime interruption revision."
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
            await output.WriteLineAsync($"Device: {DeviceText.Sanitize(information.Identity)}; Profile: {DeviceText.Sanitize(information.ProfileId)}; Firmware: {DeviceText.Sanitize(information.Version ?? "unknown")}; Reported patterns: {string.Join(", ", information.SupportedPatterns)}; ConnectionState: {device.ConnectionState}; {(simulatorControl ? "simulator control available" : "motion control unavailable")}.");
            var state = getReported();
            await output.WriteLineAsync(state is null
                ? "No reported feedback."
                : $"Reported at {state.Timestamp:O}: state {state.MotionSettings.State}, effective speed {state.MotionSettings.Speed}%, stroke {state.MotionSettings.Stroke}%, depth {state.MotionSettings.Depth}%, pattern {state.MotionSettings.Pattern}, position {state.MotionSettings.Position} mm; velocity {(state.VelocityMillimetresPerSecond is { } velocity ? $"{velocity} mm/s" : "unknown")}.");
            if (getFault() is { } errorState)
                await error.WriteLineAsync($"Device feedback error: {DeviceText.Sanitize(errorState.Message)}");
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
        var invocation = new InvocationConfiguration { ProcessTerminationTimeout = null };

        while (!quit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await input
                .ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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

            await parsed.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
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

    private void ValidateSet(
        CommandResult result,
        Option<int> speed,
        Option<int> stroke,
        Option<int> depth,
        Option<string> pattern)
    {
        if (result.Errors.Any())
            return;

        int strokeValue;
        int depthValue;
        int speedValue;
        string? name;
        try
        {
            strokeValue = result.GetValue(stroke);
            depthValue = result.GetValue(depth);
            speedValue = result.GetValue(speed);
            name = result.GetValue(pattern);
        }
        catch (InvalidOperationException exception)
        {
            result.AddError(exception.Message);
            return;
        }

        if (strokeValue > depthValue)
            result.AddError("Stroke must not exceed depth.");

        if (speedValue > 0 && strokeValue == 0)
            result.AddError("Nonzero speed requires a positive stroke.");

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
