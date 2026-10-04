using Microsoft.Extensions.Logging;
using OssmSharp.Runtime;

namespace OssmSharp.Cli;

public sealed class ListCommand(
    IDeviceDiscovery discovery,
    [FromKeyedServices("output")] TextWriter output,
    [FromKeyedServices("error")] TextWriter error,
    ILogger<ListCommand> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await error.WriteLineAsync(CliStrings.DiscoveryProgress).ConfigureAwait(false);
            var devices = await discovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var device in devices)
            {
                await output.WriteLineAsync(CliStrings.DeviceListing(device)).ConfigureAwait(false);
            }

            logger.DiscoveryCompleted(devices.Count);
            if (devices.Count == 0)
            {
                await error.WriteLineAsync(CliStrings.NoDevices).ConfigureAwait(false);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.CommandCancelled("list");
            return await CommandError.TryWriteAsync(error, CliStrings.Cancellation).ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            logger.CommandFailed("list", exception.GetType().Name);
            await CommandError.TryWriteAsync(error, CliStrings.Error("discovery", CliStrings.DiscoveryFailure(exception.Message))).ConfigureAwait(false);
            return 1;
        }
    }
}
