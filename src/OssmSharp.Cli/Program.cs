using System.CommandLine;

namespace OssmSharp.Cli;

// TODO: convert to HostApplicationBuilder
internal static class Program
{
    // Run on a background thread so async code does not wait for the macOS UI loop,
    // which this console app never runs. Without this, the app will hang after it exits.
    internal static Task<int> Main(string[] args) => Task.Run(() => InvokeAsync(args, Console.Out, Console.Error));

    internal static async Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error)
    {
        var root = new RootCommand("Connect to and control an OSSM device.");
        var list = new Command("list", "Lists nearby discovered OSSM devices and their identifiers.");
        list.SetAction((_, token) => HostRunner.RunAsync(
            (services, operationToken) => services.GetRequiredService<ListCommand>().RunAsync(operationToken),
            output,
            error,
            token));
        root.Subcommands.Add(list);

        var prompt = new Command("prompt", "Create a prompt to send commands to a specific device or a nearby device.");
        var device = new Option<string?>("--device")
        {
            Description = "Device identifier (GUID). It can be obtained from running \"list\". If omitted, it will try to connect to the first discovered nearby device.",
            Arity = ArgumentArity.ExactlyOne,
        };
        device.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(result.GetValueOrDefault<string?>()))
            {
                result.AddError("--device <identifier> is required.");
            }
        });
        prompt.Options.Add(device);
        prompt.SetAction(async (parsed, token) =>
        {
            var identifier = parsed.GetValue(device);
            if (Console.IsInputRedirected)
            {
                await error.WriteLineAsync("ERROR: prompt required stdin. Run it again in a terminal without piping or redirecting stdin; stdout and stderr may be redirected.").ConfigureAwait(false);
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
                    await error.WriteLineAsync($"ERROR: {issue.Message}").ConfigureAwait(false);
                }

                await error.WriteLineAsync("Run OssmSharp.Cli --help for usage.").ConfigureAwait(false);
                return 2;
            }

            return await parsed.InvokeAsync(configuration).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CommandError.TryWriteAsync(error, $"ERROR: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

}
