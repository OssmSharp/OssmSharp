using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
using Microsoft.Extensions.Hosting;

namespace OssmSharp.Cli;

/// <summary>
/// Parses foreground operator commands while device execution is unavailable.
/// </summary>
internal static class ForegroundRepl
{
    internal static async Task<int> RunWithOwnershipAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<IDisposable?> acquireOwnership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acquireOwnership);

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

    internal static async Task<int> RunWithOwnershipAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<IDisposable?> acquireOwnership,
        Func<IOssmDevice> createDevice,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acquireOwnership);
        ArgumentNullException.ThrowIfNull(createDevice);

        IDisposable? lease;
        try
        {
            lease = acquireOwnership();
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
                    await error.WriteLineAsync("Stock BLE connection failed or enrolled identity did not match.")
                        .ConfigureAwait(false);
                    return 1;
                }

                return await RunAsync(
                    input, output, error, device,
                    () => Volatile.Read(ref reported),
                    () => Volatile.Read(ref fault),
                    cancellationToken).ConfigureAwait(false);
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
            await error.WriteLineAsync($"Stock BLE connection failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            async ValueTask CleanupAsync()
            {
                if (device is not null)
                    await device.DisposeAsync().ConfigureAwait(false);
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

    internal static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        await RunAsync(input, output, error, null, () => null, () => null, cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        IOssmDevice? device,
        Func<DeviceState?> getReported,
        Func<Exception?> getFault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var quit = false;
        var root = new RootCommand("Foreground device controls.");
        var help = new Command("help", "Show available commands.");
        help.SetAction(async (_, _) =>
            await output.WriteLineAsync("Read-only connection. Commands: help, status, quit. Unavailable: set --speed N --stroke N --depth N --pattern NAME, stop, acknowledge."));
        var status = new Command("status", "Show device readiness.");
        status.SetAction(async (_, _) =>
        {
            if (device?.ConnectionState != DeviceConnectionState.Connected)
            {
                await output.WriteLineAsync("ConnectionState: Disconnected; device control unavailable.");
                return;
            }

            var information = device.Device;
            await output.WriteLineAsync($"Device: {information.Identity}; Profile: {information.ProfileId}; Firmware: {information.Version ?? "unknown"}; Reported patterns: {string.Join(", ", information.SupportedPatterns)}; ConnectionState: {device.ConnectionState}; motion control unavailable.");
            var state = getReported();
            await output.WriteLineAsync(state is null
                ? "No reported feedback."
                : $"Reported at {state.Timestamp:O}: state {state.MotionSettings.State}, effective speed {state.MotionSettings.Speed}%, stroke {state.MotionSettings.Stroke}%, depth {state.MotionSettings.Depth}%, pattern {state.MotionSettings.Pattern}, position {state.MotionSettings.Position} mm; velocity {(state.VelocityMillimetresPerSecond is { } velocity ? $"{velocity} mm/s" : "unknown") }.");
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
        set.Validators.Add(result =>
        {
            var strokeValue = result.GetValue(stroke);
            var depthValue = result.GetValue(depth);
            if (strokeValue > depthValue)
            {
                result.AddError("Stroke must not exceed depth.");
            }

            var name = result.GetValue(pattern);
            if (name is not null &&
                (!Enum.TryParse<MotionPattern>(name, ignoreCase: true, out var selected) ||
                 !Enum.IsDefined(selected)))
            {
                result.AddError("Provide a supported motion pattern name.");
            }
        });
        set.SetAction(async (_, _) => await error.WriteLineAsync("Motion unavailable: shared runtime Stop and feedback control is not implemented."));
        var stop = new Command("stop", "Request a serialized zero-speed Stop.");
        stop.SetAction(async (_, _) => await error.WriteLineAsync("Stop unavailable: Stock BLE cannot send a zero-speed command."));
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

    private static Option<int> Percentage(string name, int minimum) => new(name)
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
}
