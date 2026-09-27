using OssmSharp.Cli.Models;

namespace OssmSharp.Cli.Commands;

internal sealed class PromptCommand : Command
{
    internal PromptCommand(PromptSession session)
        : this((session ?? throw new ArgumentNullException(nameof(session))).RunAsync)
    {
    }

    internal PromptCommand(Func<PromptSelection, CancellationToken, Task<int>> runAsync)
        : base("prompt", "Open a foreground prompt for one selected device.")
    {
        ArgumentNullException.ThrowIfNull(runAsync);
        var bluetooth = new Option<string>("--bluetooth")
        {
            Description = "Platform device ID or exact, unambiguous discovered name.",
        };
        var simulator = new Option<string>("--simulator")
        {
            Description = "Executable path to launch, or tcp://127.0.0.1:<port> to attach.",
        };
        Options.Add(bluetooth);
        Options.Add(simulator);
        Validators.Add(result =>
        {
            if (result.Errors.Any())
            {
                return;
            }

            try
            {
                PromptSelection.Parse(result.GetValue(bluetooth), result.GetValue(simulator));
            }
            catch (ArgumentException exception)
            {
                result.AddError(exception.Message);
            }
        });
        SetAction(async (parse, cancellationToken) =>
        {
            try
            {
                var exitCode = await runAsync(
                    PromptSelection.Parse(parse.GetValue(bluetooth), parse.GetValue(simulator)),
                    cancellationToken).ConfigureAwait(false);
                return exitCode != 0 ? exitCode : cancellationToken.IsCancellationRequested ? 130 : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 130;
            }
        });
    }
}
