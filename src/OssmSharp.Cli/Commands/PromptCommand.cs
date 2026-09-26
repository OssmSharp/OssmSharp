using System.Text.RegularExpressions;

namespace OssmSharp.Cli.Commands;

internal sealed class PromptCommand : Command
{
    private readonly Func<string?, string?, string?, CancellationToken, Task<int>> _runAsync;

    internal PromptCommand(PromptHandler handler)
        : this((handler ?? throw new ArgumentNullException(nameof(handler))).RunAsync)
    {
    }

    internal PromptCommand(Func<string?, string?, string?, CancellationToken, Task<int>> runAsync)
        : base("prompt", "Open a foreground prompt for a selected device.")
    {
        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
        var peripheralId = new Option<string>("--peripheral-id")
        {
            Description = "Platform-specific locator of the selected stock device.",
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && !string.IsNullOrWhiteSpace(result.Tokens[0].Value))
                {
                    return result.Tokens[0].Value;
                }

                result.AddError("Provide one nonempty peripheral ID.");
                return null;
            },
        };
        var wifiMac = new Option<string>("--wifi-mac")
        {
            Description = "Enrolled Wi-Fi MAC of the selected stock device (AA:BB:CC:DD:EE:FF).",
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && Regex.IsMatch(
                    result.Tokens[0].Value, @"\A(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}\z"))
                {
                    return result.Tokens[0].Value.ToUpperInvariant();
                }

                result.AddError("Provide an enrolled Wi-Fi MAC as six colon-separated hex octets.");
                return null;
            },
        };
        var simulator = new Option<string>("--simulator")
        {
            Description = "Absolute path to the ossm-sim-bridge executable.",
            CustomParser = result =>
            {
                if (result.Tokens.Count == 1 && Path.IsPathFullyQualified(result.Tokens[0].Value) &&
                    File.Exists(result.Tokens[0].Value))
                {
                    return Path.GetFullPath(result.Tokens[0].Value);
                }

                result.AddError("Provide the absolute path to an existing simulator executable.");
                return null;
            },
        };
        Options.Add(peripheralId);
        Options.Add(wifiMac);
        Options.Add(simulator);
        Validators.Add(result =>
        {
            if (result.GetValue(simulator) is not null)
            {
                if (result.GetValue(peripheralId) is not null || result.GetValue(wifiMac) is not null)
                {
                    result.AddError("--simulator cannot be combined with --peripheral-id or --wifi-mac.");
                }
            }
            else if (result.GetValue(peripheralId) is null || result.GetValue(wifiMac) is null)
            {
                result.AddError("Stock control requires --peripheral-id and --wifi-mac.");
            }
        });
        SetAction((parse, cancellationToken) => _runAsync(
            parse.GetValue(peripheralId),
            parse.GetValue(wifiMac),
            parse.GetValue(simulator),
            cancellationToken));
    }

}
