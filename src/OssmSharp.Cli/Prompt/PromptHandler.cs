using System.CommandLine.Parsing;
using System.Globalization;
using OssmSharp.Device.Primitives;

namespace OssmSharp.Cli;

/// <summary>
/// Defines the foreground prompt for a selected, externally owned device.
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

    internal async Task<int> RunAsync(
        IOssmDevice? device,
        Func<Exception?> getFault,
        CancellationToken cancellationToken,
        bool simulatorControl = false,
        Action<MotionSettings>? recordSubmission = null)
    {
        var input = _input;
        var output = _output;
        var error = _error;

        var quit = false;
        MotionSettings? submitted = null;
        var root = new RootCommand("Foreground device controls.");
        var help = new Command("help", "Show available commands.");
        help.SetAction(async (_, _) =>
            await output.WriteLineAsync(
                "Commands: help, status, initialize, set --speed N --stroke N --depth N --pattern SimpleStroke, stop, quit. Initialize only after physical controls reach a stopped play-ready state; no remote navigation."));
        var status = new Command("status", "Show device readiness.");
        status.SetAction((_, _) =>
            ShowStatusAsync(device, simulatorControl, getFault, submitted));
        var initialize = new Command("initialize", "Reset control after Stop or physical intervention; never starts motion.");
        initialize.SetAction((_, _) => InitializeAsync(device, cancellationToken));
        var speed = Percentage("--speed", 0);
        var stroke = Percentage("--stroke", 0);
        var depth = Percentage("--depth", 0);
        var pattern = new Option<string>("--pattern") { Required = true };
        var set = new Command("set", "Set motion only when the device is initialized and ready.");
        set.Options.Add(speed);
        set.Options.Add(stroke);
        set.Options.Add(depth);
        set.Options.Add(pattern);
        set.Validators.Add(result => ValidateSet(result, speed, stroke, depth, pattern));
        set.SetAction((parse, invocationToken) =>
        {
            var operation = SetMotionAsync(
                device,
                parse.GetValue(speed),
                parse.GetValue(stroke),
                parse.GetValue(depth),
                parse.GetValue(pattern)!,
                motion =>
                {
                    submitted = motion;
                    recordSubmission?.Invoke(motion);
                },
                cancellationToken);
            // Keep the input loop responsive so Stop can revoke an issued Set.
            // The device owns serialization and settlement of every issued write.
            _ = ObserveSubmissionAsync(operation);
            return Task.CompletedTask;
        });
        var stop = new Command("stop", "Emergency Stop; initialize again before setting motion.");
        stop.SetAction((_, _) => StopAsync(device));
        var exit = new Command("quit", "Exit the foreground prompt.");
        exit.SetAction((_, _) =>
        {
            quit = true;
            return Task.FromResult(0);
        });
        root.Subcommands.Add(help);
        root.Subcommands.Add(status);
        root.Subcommands.Add(initialize);
        root.Subcommands.Add(set);
        root.Subcommands.Add(stop);
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

    private async Task ObserveSubmissionAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer command, Stop, or shutdown can supersede a pending Set.
        }
        catch (Exception exception)
        {
            try
            {
                await _error.WriteLineAsync(
                    $"Device operation did not complete: {DeviceText.Sanitize(exception.Message)}")
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The enclosing prompt has released its output; the device still
                // owns the issued I/O until settlement.
            }
        }
    }

    private async Task ShowStatusAsync(
        IOssmDevice? device,
        bool simulatorControl,
        Func<Exception?> getFault,
        MotionSettings? submitted)
    {
        if (device?.ConnectionState != DeviceConnectionState.Connected)
        {
            await _output.WriteLineAsync("ConnectionState: Disconnected; device control unavailable.");
            return;
        }

        var information = device.Device;
        await _output.WriteLineAsync($"Device: {DeviceText.Sanitize(information.Identity)}; Profile: {DeviceText.Sanitize(information.ProfileId ?? "unknown")}; Firmware: {DeviceText.Sanitize(information.Version ?? "unknown")}; Reported patterns: {string.Join(", ", information.SupportedPatterns)}; ConnectionState: {device.ConnectionState}; Readiness: {(device.IsReady ? "Ready" : "Paused")}{(simulatorControl && device.IsReady ? "; simulator control available" : string.Empty)}.");
        var state = device.LatestState;
        await _output.WriteLineAsync(state is null
            ? "No reported feedback."
            : $"Reported at {state.Timestamp:O}: state {state.MotionSettings.State}, effective speed {state.MotionSettings.Speed}%, stroke {state.MotionSettings.Stroke}%, depth {state.MotionSettings.Depth}%, pattern {state.MotionSettings.Pattern}, position {state.MotionSettings.Position} mm; velocity {(state.VelocityMillimetresPerSecond is { } velocity ? $"{velocity} mm/s" : "unknown")}.");
        await _output.WriteLineAsync(submitted is null
            ? "No submitted motion settings."
            : $"Submitted: speed {submitted.Speed}%, stroke {submitted.Stroke}%, depth {submitted.Depth}%, pattern {submitted.Pattern}; reported state is independent.");
        if (getFault() is { } errorState)
            await _error.WriteLineAsync($"Device feedback error: {DeviceText.Sanitize(errorState.Message)}");
    }

    private async Task StopAsync(IOssmDevice? device)
    {
        if (device?.ConnectionState != DeviceConnectionState.Connected)
        {
            await _error.WriteLineAsync("Stop unavailable: no connected device.").ConfigureAwait(false);
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await device.EmergencyStopAsync(timeout.Token).ConfigureAwait(false);
        await _output.WriteLineAsync("Stop write settled; device control paused until initialize; physical stop is not confirmed.").ConfigureAwait(false);
    }

    private async Task InitializeAsync(
        IOssmDevice? device,
        CancellationToken cancellationToken)
    {
        if (device?.ConnectionState != DeviceConnectionState.Connected)
        {
            await _error.WriteLineAsync("Initialization unavailable: connect a device first.");
            return;
        }

        try
        {
            await device.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _output.WriteLineAsync(
                "Zero write settled and readiness verified; only a new set command can request motion.");
        }
        catch (InvalidOperationException exception)
        {
            await _error.WriteLineAsync(
                $"Initialization unavailable: {DeviceText.Sanitize(exception.Message)} Use physical controls to reach stopped play-ready state; remote navigation is not available.");
        }
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

    private async Task SetMotionAsync(
        IOssmDevice? device,
        int speed,
        int stroke,
        int depth,
        string pattern,
        Action<MotionSettings>? recordSubmission,
        CancellationToken cancellationToken)
    {
        var output = _output;
        var error = _error;
        if (device?.ConnectionState != DeviceConnectionState.Connected || !device.IsReady)
        {
            await error.WriteLineAsync("Motion unavailable: initialize a connected, stopped play-ready device first.").ConfigureAwait(false);
            return;
        }

        var selected = Enum.Parse<MotionPattern>(pattern, ignoreCase: true);
        if (!device.Device.SupportedPatterns.Contains(selected))
        {
            await error.WriteLineAsync("The selected device does not support this pattern.").ConfigureAwait(false);
            return;
        }

        var current = device.LatestState!.MotionSettings;
        var desired = current with
        {
            Speed = speed,
            Stroke = stroke,
            Depth = depth,
            Sensation = 50,
            Buffer = 0,
            Pattern = selected,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await device.SetAsync(desired, timeout.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync($"Motion unavailable: {DeviceText.Sanitize(exception.Message)}")
                .ConfigureAwait(false);
            return;
        }

        recordSubmission?.Invoke(desired);
        await output.WriteLineAsync("Device write settled; check status for reported state.").ConfigureAwait(false);
    }

}
