namespace OssmSharp.Cli.Commands;

public sealed class DeviceGetCommand : Command
{
    internal DeviceGetCommand(TextWriter output, TextWriter error)
        : this((peripheralId, cancellationToken) => ReadIdentityAsync(
            peripheralId, output, error, cancellationToken))
    {
    }

    internal DeviceGetCommand(Func<string?, CancellationToken, Task<int>> readIdentity)
        : base("get", "Read a selected stock device's identity without motion writes.")
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
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
        Options.Add(peripheralId);
        SetAction((parse, cancellationToken) => readIdentity(
            parse.GetValue(peripheralId), cancellationToken));
    }

    private static async Task<int> ReadIdentityAsync(
        string? peripheralId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
#if MACOS
        var identity = await StockIdentityReader.ReadAsync(
            peripheralId,
            Console.In,
            error,
            !Console.IsInputRedirected && !Console.IsErrorRedirected,
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(identity.AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
#else
        await error.WriteLineAsync("Read-only BLE identification requires the macOS-targeted CLI build.")
            .ConfigureAwait(false);
        return 1;
#endif
    }
}
