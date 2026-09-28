using System.CommandLine;
using System.Globalization;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli;

internal sealed class PromptHandler
{
    private const int MaximumPendingCommands = 32;
    private readonly IOssmDevice device;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly bool interactive;
    private readonly HashSet<Task> operations = [];
    private readonly TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RootCommand commands;
    private readonly Argument<int> pattern = new("pattern")
    {
        Description = "Nonnegative integer pattern identifier, passed unchanged to firmware.",
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            {
                return parsed;
            }

            result.AddError($"pattern '{value}' must be an integer between 0 and {int.MaxValue}.");
            return 0;
        },
    };
    private readonly Argument<double> speed = Percentage("speed");
    private readonly Argument<double> stroke = Percentage("stroke");
    private readonly Argument<double> depth = Percentage("depth");
    private readonly Argument<double> sensation = Percentage("sensation");
    private CancellationToken sessionToken;
    private Task? stopTask;
    private bool quitting;
    private int exitCode;

    internal PromptHandler(IOssmDevice device, TextReader input, TextWriter output, TextWriter error, bool interactive = false)
    {
        this.device = device;
        this.input = input;
        this.output = TextWriter.Synchronized(output);
        this.error = TextWriter.Synchronized(error);
        this.interactive = interactive;
        commands = CreateCommands();
    }

    private string ArmedStatus => $"Armed: {(device.IsArmed ? "yes" : "no")}.";

    internal async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sessionToken = session.Token;
        device.StateChanged += StateChanged;
        Task<string?>? read = null;
        try
        {
            StateChanged(this, EventArgs.Empty);
            while (!quitting && !terminal.Task.IsCompleted)
            {
                operations.RemoveWhere(operation => operation.IsCompleted);
                if (interactive)
                {
                    await error.WriteAsync("ossm> ").ConfigureAwait(false);
                    await error.FlushAsync(sessionToken).ConfigureAwait(false);
                }

                read = input.ReadLineAsync(sessionToken).AsTask();
                await Task.WhenAny(read, terminal.Task).ConfigureAwait(false);
                if (terminal.Task.IsCompleted)
                {
                    break;
                }

                var completedRead = read;
                read = null;
                var line = await completedRead.ConfigureAwait(false);
                if (line is null)
                {
                    Quit();
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Length > 4096)
                {
                    await error.WriteLineAsync($"ERROR input: command exceeds 4096 characters. Nothing sent. {ArmedStatus}").ConfigureAwait(false);
                    continue;
                }

                var parsed = commands.Parse(line);
                if (parsed.Errors.Count > 0)
                {
                    if (parsed.CommandResult.Command.Name == "set")
                    {
                        Argument[] arguments = [pattern, speed, stroke, depth, sensation];
                        var missing = arguments.Where(argument => parsed.GetResult(argument) is not { Tokens.Count: > 0 }).Select(argument => argument.Name).ToArray();
                        if (missing.Length > 0)
                        {
                            await error.WriteLineAsync($"ERROR set: missing {string.Join(", ", missing)}; expected set <pattern> <speed> <stroke> <depth> <sensation>. Nothing sent. {ArmedStatus}").ConfigureAwait(false);
                        }
                    }

                    foreach (var issue in parsed.Errors)
                    {
                        await error.WriteLineAsync($"ERROR input: {issue.Message} Nothing sent. {ArmedStatus}").ConfigureAwait(false);
                    }

                    continue;
                }

                await parsed.InvokeAsync(
                    new InvocationConfiguration
                    {
                        Output = output,
                        Error = error,
                        EnableDefaultExceptionHandler = false,
                        ProcessTerminationTimeout = null,
                    },
                    sessionToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            if (!await CommandError.TryWriteAsync(error, "Session cancelled; awaiting issued work.").ConfigureAwait(false))
            {
                Interlocked.Exchange(ref exitCode, 1);
            }
        }
        catch (Exception exception)
        {
            await FailAsync("input", exception).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await session.CancelAsync().ConfigureAwait(false);
                if (read is not null)
                {
                    try
                    {
                        await read.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        await FailAsync("input", exception).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                try
                {
                    await Task.WhenAll(operations).ConfigureAwait(false);
                }
                finally
                {
                    device.StateChanged -= StateChanged;
                }
            }
        }

        if (terminal.Task.IsCompleted)
        {
            await CommandError.TryWriteAsync(error, $"Session ended after a failure. {ArmedStatus} Run OssmSharp.Cli prompt to reconnect.").ConfigureAwait(false);
        }

        return exitCode;
    }

    private static Argument<double> Percentage(string name) => new(name)
    {
        Description = "Finite percentage between 0 and 100, using a decimal point.",
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed)
                && parsed is >= 0 and <= 100)
            {
                return parsed;
            }

            result.AddError($"{name} '{value}' must be a finite number between 0 and 100.");
            return 0;
        },
    };

    private RootCommand CreateCommands()
    {
        var root = new RootCommand("Send firmware settings; completed writes do not verify physical motion.");
        var set = new Command("set", "Submit all five settings: pattern speed stroke depth sensation.");
        set.Arguments.Add(pattern);
        set.Arguments.Add(speed);
        set.Arguments.Add(stroke);
        set.Arguments.Add(depth);
        set.Arguments.Add(sensation);
        set.SetAction(Set);
        root.Subcommands.Add(set);

        var stop = new Command("stop", "Disarm, discard pending settings, and send speed zero after issued I/O settles.");
        stop.SetAction(_ => Stop());
        root.Subcommands.Add(stop);
        var reset = new Command("reset", "Explicitly arm without resending previous settings.");
        reset.SetAction(_ => Reset());
        root.Subcommands.Add(reset);
        var status = new Command("status", "Display last known state and arming status.");
        var live = new Option<bool>("--live") { Description = "Read device state instead of using the cache." };
        status.Options.Add(live);
        status.SetAction(parsed => Track("status", () => StatusAsync(parsed.GetValue(live))));
        root.Subcommands.Add(status);
        var help = new Command("help", "List commands and input ranges.");
        help.SetAction(_ => Help());
        root.Subcommands.Add(help);
        var quit = new Command("quit", "End input and await issued operations and cleanup.");
        quit.SetAction(_ => Quit());
        root.Subcommands.Add(quit);
        return root;
    }

    private void Set(ParseResult parsed)
    {
        var settings = new MotionSettings(parsed.GetValue(pattern), parsed.GetValue(speed), parsed.GetValue(stroke), parsed.GetValue(depth), parsed.GetValue(sensation));
        Track("set", async () =>
        {
            SettingsResult result;
            try
            {
                result = await device.SetAsync(settings, sessionToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (device.ConnectionFailure is null && exception is not ObjectDisposedException)
            {
                await error.WriteLineAsync($"ERROR set: {exception.Message} Nothing sent. {ArmedStatus}").ConfigureAwait(false);
                return;
            }

            var message = result.Outcome switch
            {
                SettingsOutcome.Completed => "OK set: all control commands sent; physical motion not verified.",
                SettingsOutcome.Superseded => $"SUPERSEDED set: {result.CommandsWritten} commands sent; issued commands may have taken effect.",
                _ => $"CANCELLED set: {result.CommandsWritten} commands sent; issued commands may have taken effect.",
            };
            await output.WriteLineAsync($"{message} {ArmedStatus}").ConfigureAwait(false);
        });
    }

    private void Stop()
    {
        if (stopTask is { IsCompleted: false })
        {
            error.WriteLine($"Stop already pending; awaiting issued I/O. {ArmedStatus}");
            return;
        }

        stopTask = ObserveAsync("stop", async () =>
        {
            var pending = device.EmergencyStopAsync(CancellationToken.None);
            try
            {
                await error.WriteLineAsync($"Stop requested; pending settings cancelled, issued I/O still awaited. {ArmedStatus}").ConfigureAwait(false);
            }
            finally
            {
                await pending.ConfigureAwait(false);
            }

            await output.WriteLineAsync($"OK stop: Stop command sent; pending requests cleared. {ArmedStatus}").ConfigureAwait(false);
        });
        operations.Add(stopTask);
    }

    private void Reset() => Track("reset", async () =>
    {
        try
        {
            await device.ResetAsync(sessionToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (device.ConnectionFailure is null && exception is not ObjectDisposedException)
        {
            await error.WriteLineAsync($"ERROR reset: {exception.Message} Nothing sent. {ArmedStatus}").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"OK reset: No motion command sent. {ArmedStatus}").ConfigureAwait(false);
    });

    private async Task StatusAsync(bool bypassCache)
    {
        var state = await device.GetStateAsync(bypassCache, sessionToken).ConfigureAwait(false);
        var origin = bypassCache ? "live" : "cached";
        if (state is null)
        {
            await output.WriteLineAsync($"OK status ({origin}): no last known state. {ArmedStatus}").ConfigureAwait(false);
            return;
        }

        var settings = state.Settings;
        await output.WriteLineAsync(FormattableString.Invariant($"OK status ({origin}): pattern={settings.Pattern} speed={settings.Speed} stroke={settings.Stroke} depth={settings.Depth} sensation={settings.Sensation}. {ArmedStatus}")).ConfigureAwait(false);
    }

    private void Help()
    {
        commands.Parse("--help").Invoke(new InvocationConfiguration { Output = output, Error = error });
        output.WriteLine("set <pattern> <speed> <stroke> <depth> <sensation>");
        output.WriteLine($"pattern: integer 0..{int.MaxValue}; speed, stroke, depth, sensation: finite 0..100 (decimal point).");
        output.WriteLine($"Connect never arms; reset sends no settings. {ArmedStatus}");
    }

    private void Quit() => quitting = true;

    private void Track(string operation, Func<Task> action)
    {
        operations.RemoveWhere(task => task.IsCompleted);
        if (operations.Count >= MaximumPendingCommands)
        {
            error.WriteLine($"ERROR {operation}: command queue is full; wait for completion. Stop remains available. Nothing sent. {ArmedStatus}");
            return;
        }

        operations.Add(ObserveAsync(operation, action));
    }

    private async Task ObserveAsync(string operation, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            try
            {
                await output.WriteLineAsync($"CANCELLED {operation}: unissued work cancelled; issued work awaited. {ArmedStatus}").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await FailAsync(operation, exception).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await FailAsync(operation, exception).ConfigureAwait(false);
        }
    }

    private Task FailAsync(string operation, Exception exception)
    {
        Interlocked.Exchange(ref exitCode, 1);
        terminal.TrySetResult();
        var detail = exception is DeviceWriteException write
            ? $"{write.Command.Control} write failed after {write.CommandsWritten} completed commands; settings may be partially applied."
            : $"{exception.Message} Device state may be unknown.";
        return CommandError.TryWriteAsync(error, $"ERROR {operation}: {detail} No rollback claimed. {ArmedStatus}");
    }

    private void StateChanged(object? sender, EventArgs args)
    {
        if (device.ConnectionFailure is not null)
        {
            Interlocked.Exchange(ref exitCode, 1);
            terminal.TrySetResult();
        }
    }
}
