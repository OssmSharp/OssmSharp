using System.CommandLine;
using System.Globalization;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

// TODO: refactor this omnibus class
internal sealed class PromptHandler : IPromptHandler
{
    private static readonly StrokePattern[] Patterns = Enum.GetValues<StrokePattern>();
    private static readonly string PatternChoices = string.Join(", ", Patterns.Select(CliStrings.PatternChoice));
    private readonly IOssmDevice device;
    private readonly IPromptInput input;
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly PromptConsole? console;
    private readonly PromptInterruptState interruptState;
    private readonly string success;
    private readonly string warning;
    private readonly string failure;
    private readonly string outputReset;
    private readonly string errorReset;
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

    internal PromptHandler(
        IOssmDevice device,
        IPromptInput input,
        TextWriter output,
        TextWriter error,
        PromptConsole? console = null,
        PromptInterruptState? interruptState = null)
    {
        this.device = device;
        this.input = input;
        this.output = console?.Output ?? TextWriter.Synchronized(output);
        this.error = console?.Error ?? TextWriter.Synchronized(error);
        this.console = console;
        this.interruptState = interruptState ?? new PromptInterruptState();
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
        Task<PromptInputResult>? read = null;
        try
        {
            StateChanged(this, EventArgs.Empty);
            while (!quitting && !terminal.Task.IsCompleted)
            {
                read = input.ReadAsync(sessionToken);
                if (!terminal.Task.IsCompleted && !read.IsCompleted)
                {
                    await Task.WhenAny(read, terminal.Task).ConfigureAwait(false);
                }

                if (terminal.Task.IsCompleted)
                {
                    break;
                }

                var completedRead = read;
                read = null;
                var inputResult = await completedRead.ConfigureAwait(false);
                if (inputResult.IsInterrupted)
                {
                    await QuitAsync().ConfigureAwait(false);
                    break;
                }

                sessionToken.ThrowIfCancellationRequested();

                var line = inputResult.Text;
                if (line is null)
                {
                    await QuitAsync().ConfigureAwait(false);
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

                await InvokeAsync(parsed, inputResult.CommandCancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            if (!await CommandError.TryWriteAsync(error, CliStrings.Cancellation).ConfigureAwait(false))
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
                device.StateChanged -= StateChanged;
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
        Description = name == "speed" ? CliStrings.SpeedHelp : CliStrings.PercentageHelp,
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

    private async Task InvokeAsync(ParseResult parsed, CancellationToken commandCancellationToken)
    {
        interruptState.BeginCommand();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, commandCancellationToken);
        try
        {
            var invocation = parsed.InvokeAsync(
                new InvocationConfiguration
                {
                    Output = output,
                    Error = error,
                    EnableDefaultExceptionHandler = false,
                    ProcessTerminationTimeout = null,
                },
                operation.Token);
            interruptState.TrackCommand(invocation);
            if (!commandCancellationToken.CanBeCanceled)
            {
                await invocation.ConfigureAwait(false);
                return;
            }

            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = commandCancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                interrupted);
            if (await Task.WhenAny(invocation, interrupted.Task).ConfigureAwait(false) == interrupted.Task
                && commandCancellationToken.IsCancellationRequested)
            {
                var interruptedOperation = Task.WhenAll(invocation, StopAsync());
                interruptState.TrackCommand(interruptedOperation);
                await interruptedOperation.ConfigureAwait(false);
                return;
            }

            await invocation.ConfigureAwait(false);
        }
        finally
        {
            interruptState.EndCommand();
        }
    }

    private RootCommand CreateCommands()
    {
        var root = new RootCommand(CliStrings.RootHelp);
        var set = new Command("set", CliStrings.SetHelp);
        set.Arguments.Add(pattern);
        set.Arguments.Add(speed);
        set.Arguments.Add(stroke);
        set.Arguments.Add(depth);
        set.Arguments.Add(sensation);
        set.SetAction(SetAsync);
        root.Subcommands.Add(set);

        var stop = new Command("stop", CliStrings.StopHelp);
        stop.SetAction((_, _) => StopAsync());
        root.Subcommands.Add(stop);
        var reset = new Command("reset", CliStrings.ResetHelp);
        reset.SetAction(ResetAsync);
        root.Subcommands.Add(reset);
        var status = new Command("status", CliStrings.StatusHelp);
        status.SetAction((_, token) => ObserveAsync("status", () => StatusAsync(token), token));
        root.Subcommands.Add(status);
        var help = new Command("help", CliStrings.HelpHelp);
        root.Subcommands.Add(help);
        var quit = new Command("quit", CliStrings.QuitHelp);
        quit.SetAction((_, _) => QuitAsync());
        root.Subcommands.Add(quit);
        var topic = new Argument<string?>("command") { Description = CliStrings.HelpArgument, Arity = ArgumentArity.ZeroOrOne };
        topic.AcceptOnlyFromAmong([.. root.Subcommands.Select(command => command.Name)]);
        help.Arguments.Add(topic);
        help.SetAction(parsed => Help(parsed.GetValue(topic)));
        return root;
    }

    private Task SetAsync(ParseResult parsed, CancellationToken cancellationToken)
    {
        var settings = new MotionSettings(parsed.GetValue(pattern), parsed.GetValue(speed), parsed.GetValue(stroke), parsed.GetValue(depth), parsed.GetValue(sensation));
        return ObserveAsync(
            "set",
            async () =>
            {
                SetResult result;
                try
                {
                    result = await device.SetAsync(settings, cancellationToken).ConfigureAwait(false);
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
            },
            cancellationToken);
    }

    private async Task StopAsync()
    {
        if (stopTask is { IsCompleted: false })
        {
            await error.WriteLineAsync(CliStrings.StopInProgress).ConfigureAwait(false);
            await stopTask.ConfigureAwait(false);
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
        await stopTask.ConfigureAwait(false);
    }

    private Task ResetAsync(ParseResult _, CancellationToken cancellationToken) => ObserveAsync(
        "reset",
        async () =>
        {
            try
            {
                await device.ResetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (device.ConnectionFailure is null && exception is not ObjectDisposedException)
            {
                await error.WriteLineAsync(CliStrings.Error("reset", exception.Message, failure, errorReset)).ConfigureAwait(false);
                return;
            }

            await output.WriteLineAsync(CliStrings.ResetCompleted(success, outputReset)).ConfigureAwait(false);
        },
        cancellationToken);

    private async Task StatusAsync(CancellationToken cancellationToken)
    {
        var state = await device.GetStateAsync(bypassCache: true, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(CliStrings.Status(state!, success, outputReset)).ConfigureAwait(false);
    }

    private void Help(string? command)
    {
        commands.Parse(command is null ? ["--help"] : [command, "--help"]).Invoke(new InvocationConfiguration { Output = output, Error = error });
    }

    private async Task QuitAsync()
    {
        await StopAsync().ConfigureAwait(false);
        quitting = true;
    }

    private async Task ObserveAsync(string operation, Func<Task> action, CancellationToken cancellationToken = default)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
