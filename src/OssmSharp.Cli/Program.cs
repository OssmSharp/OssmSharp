using System.CommandLine;

namespace OssmSharp.Cli;

internal static class Program
{
    // Run on a background thread so async code does not wait for the macOS UI loop,
    // which this console app never runs. Without this, the app will hang after it exits.
    internal static Task<int> Main(string[] args) => Task.Run(() => InvokeAsync(args, Console.Out, Console.Error));

    internal static async Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error)
    {
        var root = new RootCommand(CliStrings.RootHelp);
        var list = new Command("list", CliStrings.ListHelp);
        list.SetAction((_, token) => HostRunner.RunAsync(
            (services, operationToken) => services.GetRequiredService<ListCommand>().RunAsync(operationToken),
            output,
            error,
            token));
        root.Subcommands.Add(list);

        var prompt = new Command("prompt", CliStrings.PromptHelp);
        var device = new Option<string?>("--device")
        {
            Description = CliStrings.DeviceHelp,
            Arity = ArgumentArity.ExactlyOne,
        };
        device.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(result.GetValueOrDefault<string?>()))
            {
                result.AddError(CliStrings.MissingOptionValue("--device", "<identifier>"));
            }
        });
        prompt.Options.Add(device);
        prompt.SetAction(async (parsed, token) =>
        {
            var identifier = parsed.GetValue(device);
            if (Console.IsInputRedirected)
            {
                await error.WriteLineAsync(CliStrings.Error("prompt", CliStrings.RedirectedInput)).ConfigureAwait(false);
                return 1;
            }

            return await HostRunner.RunAsync(
                (services, operationToken) => services.GetRequiredService<PromptCommand>().RunAsync(identifier, operationToken),
                output,
                error,
                token).ConfigureAwait(false);
        });
        root.Subcommands.Add(prompt);

        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
        };

        try
        {
            var parsed = root.Parse(args);
            if (parsed.Errors.Count > 0)
            {
                foreach (var issue in parsed.Errors)
                {
                    await error.WriteLineAsync(CliStrings.Error("input", issue.Message)).ConfigureAwait(false);
                }

                await error.WriteLineAsync(CliStrings.UsageHint).ConfigureAwait(false);
                return 2;
            }

            return await parsed.InvokeAsync(configuration).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CommandError.TryWriteAsync(error, CliStrings.Error("command", exception.Message)).ConfigureAwait(false);
            return 1;
        }
    }

}
