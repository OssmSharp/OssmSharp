using System.CommandLine;
using System.Globalization;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

// TODO: refactor this omnibus class
internal sealed class PromptHandler : IPromptHandler
{
    private const int MaximumPendingCommands = 32;
    private static readonly StrokePattern[] Patterns = Enum.GetValues<StrokePattern>();
    private static readonly string PatternChoices = string.Join(", ", Patterns.Select(CliStrings.PatternChoice));
    private readonly IOssmDevice device;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly IPromptConsole? console;
    private readonly string success;
    private readonly string warning;
    private readonly string failure;
    private readonly string outputReset;
    private readonly string errorReset;
    private readonly HashSet<Task> operations = [];
    private readonly TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RootCommand commands;
    private readonly Argument<StrokePattern> pattern = new("pattern")
    {
        Description = CliStrings.PatternHelp((int)Patterns[0], (int)Patterns[^1], PatternChoices),
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            foreach (var candidate in Patterns)
            {
                if (value == ((int)candidate).ToString(CultureInfo.InvariantCulture)
                    || value.Equals(candidate.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            result.AddError(CliStrings.InvalidPattern(value, PatternChoices));
            return default;
        },
    };
    private readonly Argument<int> speed = Percentage("speed");
    private readonly Argument<int> stroke = Percentage("stroke");
    private readonly Argument<int> depth = Percentage("depth");
    private readonly Argument<int> sensation = Percentage("sensation");
    private CancellationToken sessionToken;
    private Task? stopTask;
    private bool quitting;
    private int exitCode;

    internal PromptHandler(IOssmDevice device, TextReader input, TextWriter output, TextWriter error, IPromptConsole? console = null)
    {
        this.device = device;
        this.input = input;
        this.output = console?.Output ?? TextWriter.Synchronized(output);
        this.error = console?.Error ?? TextWriter.Synchronized(error);
        this.console = console;
        success = console?.Success ?? string.Empty;
        warning = console?.Warning ?? string.Empty;
        failure = console?.Failure ?? string.Empty;
        outputReset = console?.OutputReset ?? string.Empty;
        errorReset = console?.ErrorReset ?? string.Empty;
        commands = CreateCommands();
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
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
                console?.BeginRead();
                read = input.ReadLineAsync(sessionToken).AsTask();
                await Task.WhenAny(read, terminal.Task).ConfigureAwait(false);
                if (terminal.Task.IsCompleted)
                {
                    break;
                }

                var completedRead = read;
                read = null;
                var line = await completedRead.ConfigureAwait(false);
                console?.EndRead();
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
                    await error.WriteLineAsync(CliStrings.Error("input", CliStrings.OversizedInput, failure, errorReset)).ConfigureAwait(false);
                    continue;
                }

                var parsed = commands.Parse(line);
                if (parsed.Errors.Count > 0)
                {
                    if (parsed.CommandResult.Command.Name == "set")
                    {
                        var command = parsed.CommandResult.Command;
                        var arguments = command.Arguments;
                        var missing = arguments.Where(argument => parsed.GetResult(argument) is not { Tokens.Count: > 0 }).Select(argument => argument.Name).ToArray();
                        if (missing.Length > 0)
                        {
                            var usage = $"{command.Name} {string.Join(" ", arguments.Select(argument => $"<{argument.Name}>"))}";
                            await error.WriteLineAsync(CliStrings.Error("set", CliStrings.MissingArguments(string.Join(", ", missing), usage), failure, errorReset)).ConfigureAwait(false);
                            continue;
                        }
                    }

                    foreach (var issue in parsed.Errors)
                    {
                        await error.WriteLineAsync(CliStrings.Error("input", issue.Message, failure, errorReset)).ConfigureAwait(false);
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
            console?.EndRead();
            if (!await CommandError.TryWriteAsync(error, CliStrings.Cancellation).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref exitCode, 1);
            }
        }
        catch (Exception exception)
        {
            console?.EndRead();
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
                    console?.EndRead();
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
        }

        if (terminal.Task.IsCompleted)
        {
            await CommandError.TryWriteAsync(error, CliStrings.SessionEnded).ConfigureAwait(false);
        }

        return exitCode;
    }

    private static Argument<int> Percentage(string name) => new(name)
    {
        Description = CliStrings.PercentageHelp,
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= 0 and <= 100)
            {
                return parsed;
            }

            result.AddError(CliStrings.InvalidPercentage(name, value));
            return 0;
        },
    };

    private RootCommand CreateCommands()
    {
        var root = new RootCommand(CliStrings.RootHelp);
        var set = new Command("set", CliStrings.SetHelp);
        set.Arguments.Add(pattern);
        set.Arguments.Add(speed);
        set.Arguments.Add(stroke);
        set.Arguments.Add(depth);
        set.Arguments.Add(sensation);
        set.SetAction(Set);
        root.Subcommands.Add(set);

        var stop = new Command("stop", CliStrings.StopHelp);
        stop.SetAction(_ => Stop());
        root.Subcommands.Add(stop);
        var reset = new Command("reset", CliStrings.ResetHelp);
        reset.SetAction(_ => Reset());
        root.Subcommands.Add(reset);
        var status = new Command("status", CliStrings.StatusHelp);
        status.SetAction(_ => Track("status", StatusAsync));
        root.Subcommands.Add(status);
        var help = new Command("help", CliStrings.HelpHelp);
        root.Subcommands.Add(help);
        var quit = new Command("quit", CliStrings.QuitHelp);
        quit.SetAction(_ => Quit());
        root.Subcommands.Add(quit);
        var topic = new Argument<string?>("command") { Description = CliStrings.HelpArgument, Arity = ArgumentArity.ZeroOrOne };
        topic.AcceptOnlyFromAmong([.. root.Subcommands.Select(command => command.Name)]);
        help.Arguments.Add(topic);
        help.SetAction(parsed => Help(parsed.GetValue(topic)));
        return root;
    }

    private void Set(ParseResult parsed)
    {
        var settings = new MotionSettings(parsed.GetValue(pattern), parsed.GetValue(speed), parsed.GetValue(stroke), parsed.GetValue(depth), parsed.GetValue(sensation));
        Track("set", async () =>
        {
            SetResult result;
            try
            {
                result = await device.SetAsync(settings, sessionToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (device.ConnectionFailure is null && exception is not ObjectDisposedException)
            {
                var detail = device.IsArmed ? exception.Message : CliStrings.DisarmedSet;
                await error.WriteLineAsync(CliStrings.Error("set", detail, failure, errorReset)).ConfigureAwait(false);
                return;
            }

            var message = result.Status == SetStatus.Completed
                ? CliStrings.SetCompleted(success, outputReset)
                : CliStrings.SetInterrupted(result, warning, outputReset);
            await output.WriteLineAsync(message).ConfigureAwait(false);
        });
    }

    private void Stop()
    {
        if (stopTask is { IsCompleted: false })
        {
            error.WriteLine(CliStrings.StopInProgress);
            return;
        }

        stopTask = ObserveAsync("stop", async () =>
        {
            var pending = device.EmergencyStopAsync(CancellationToken.None);
            try
            {
                await error.WriteLineAsync(CliStrings.StopProgress).ConfigureAwait(false);
            }
            finally
            {
                await pending.ConfigureAwait(false);
            }

            await output.WriteLineAsync(CliStrings.StopCompleted(success, outputReset)).ConfigureAwait(false);
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
            await error.WriteLineAsync(CliStrings.Error("reset", exception.Message, failure, errorReset)).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(CliStrings.ResetCompleted(success, outputReset)).ConfigureAwait(false);
    });

    private async Task StatusAsync()
    {
        var state = await device.GetStateAsync(bypassCache: true, sessionToken).ConfigureAwait(false);
        await output.WriteLineAsync(CliStrings.Status(state!, success, outputReset)).ConfigureAwait(false);
    }

    private void Help(string? command)
    {
        commands.Parse(command is null ? ["--help"] : [command, "--help"]).Invoke(new InvocationConfiguration { Output = output, Error = error });
    }

    // RunAsync awaits the Stop before returning.
    private void Quit()
    {
        Stop();
        quitting = true;
    }

    private void Track(string operation, Func<Task> action)
    {
        operations.RemoveWhere(task => task.IsCompleted);
        if (operations.Count >= MaximumPendingCommands)
        {
            error.WriteLine(CliStrings.Error(operation, CliStrings.QueueFull, failure, errorReset));
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
                await output.WriteLineAsync(CliStrings.OperationCancelled(operation, warning, outputReset)).ConfigureAwait(false);
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
            ? CliStrings.WriteFailure(write)
            : exception.Message;
        return CommandError.TryWriteAsync(error, CliStrings.Error(operation, detail, failure, errorReset));
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
