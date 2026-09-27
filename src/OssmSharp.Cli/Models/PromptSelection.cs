using System.ComponentModel.DataAnnotations;
using OssmSharp.Cli.Interop;
using OssmSharp.Device.Simulator.Models;

namespace OssmSharp.Cli.Models;

internal sealed record PromptSelection
{
    private PromptSelection(PromptMode mode, string value)
    {
        Mode = mode;
        Value = value;
    }

    internal PromptMode Mode { get; }

    internal string Value { get; }

    internal static PromptSelection Parse(string? bluetooth, string? simulator)
    {
        if (bluetooth is not null && simulator is not null)
        {
            throw new ArgumentException("Choose either --bluetooth or --simulator, not both.");
        }

        if (bluetooth is not null)
        {
            return !string.IsNullOrWhiteSpace(bluetooth)
                ? new PromptSelection(PromptMode.Bluetooth, bluetooth)
                : throw new ArgumentException("Provide a nonempty device ID or name for --bluetooth.");
        }

        if (simulator is null)
        {
            throw new ArgumentException("Choose --bluetooth <id-or-name> or --simulator <executable-or-local-endpoint>.");
        }

        if (string.IsNullOrWhiteSpace(simulator))
        {
            throw new ArgumentException("Provide an executable path or local endpoint for --simulator.");
        }

        if (simulator.Contains("://", StringComparison.Ordinal)
            || simulator.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            var options = new SimulatedDeviceOptions { Endpoint = simulator };
            if (!Uri.TryCreate(simulator, UriKind.Absolute, out _)
                || !Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true))
            {
                throw new ArgumentException("The simulator endpoint must be tcp://127.0.0.1:<port> (port 1-65535).");
            }

            return new PromptSelection(PromptMode.SimulatorAttach, simulator);
        }

        var path = Path.GetFullPath(simulator);
        if (!ExecutableFile.IsExecutable(path))
        {
            throw new ArgumentException("The simulator path must identify an existing executable.");
        }

        return new PromptSelection(PromptMode.SimulatorLaunch, path);
    }
}
