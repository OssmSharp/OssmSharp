using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

public sealed class ListCommand(
    DeviceDiscovery discovery,
    [FromKeyedServices("output")] TextWriter output,
    [FromKeyedServices("error")] TextWriter error,
    ILogger<ListCommand> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await error.WriteLineAsync("Searching for OSSM devices ...").ConfigureAwait(false);
            var devices = await discovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var device in devices)
            {
                await output.WriteLineAsync($"{device.Identifier}  {device.Name}").ConfigureAwait(false);
            }

            logger.DiscoveryCompleted(devices.Count);
            if (devices.Count == 0)
            {
                await error.WriteLineAsync("No devices found.").ConfigureAwait(false);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.CommandCancelled("list");
            return await CommandError.TryWriteAsync(error, "Discovery cancelled.").ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            logger.CommandFailed("list", exception.GetType().Name);
            await CommandError.TryWriteAsync(error, $"ERROR discovery: {exception.Message} Check Bluetooth access and retry OssmSharp.Cli list.").ConfigureAwait(false);
            return 1;
        }
    }
}
