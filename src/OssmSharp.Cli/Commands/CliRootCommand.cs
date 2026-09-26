namespace OssmSharp.Cli.Commands;

internal sealed class CliRootCommand : RootCommand
{
    internal CliRootCommand(DeviceCommand device, PromptCommand prompt)
        : base("Command-line interface for OSSM devices.")
    {
        Subcommands.Add(device);
        Subcommands.Add(prompt);
    }
}
