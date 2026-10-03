using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

internal sealed class PromptHandlerFactory(
    IPromptInput input,
    [FromKeyedServices("output")] TextWriter output,
    [FromKeyedServices("error")] TextWriter error,
    PromptConsole console,
    PromptInterruptState interruptState)
{
    internal IPromptHandler Create(IOssmDevice device) => new PromptHandler(device, input, output, error, console, interruptState);
}
