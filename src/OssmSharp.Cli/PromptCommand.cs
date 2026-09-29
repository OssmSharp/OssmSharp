using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

public sealed class PromptCommand
{
    private readonly DeviceDiscovery discovery;
    private readonly Func<string, IOssmDevice> createDevice;
    private readonly Func<IOssmDevice, PromptHandler> createPrompt;
    private readonly TextWriter error;
    private readonly ILogger<PromptCommand> logger;

    internal PromptCommand(
        DeviceDiscovery discovery,
        Func<string, IOssmDevice> createDevice,
        Func<IOssmDevice, PromptHandler> createPrompt,
        [FromKeyedServices("error")] TextWriter error,
        ILogger<PromptCommand> logger)
    {
        this.discovery = discovery;
        this.createDevice = createDevice;
        this.createPrompt = createPrompt;
        this.error = error;
        this.logger = logger;
    }

    public async Task<int> RunAsync(string? identifier, CancellationToken cancellationToken = default)
    {
        IOssmDevice? device = null;
        var exitCode = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = "OSSM";
            if (identifier is null)
            {
                var discovered = await FindDeviceAsync(cancellationToken).ConfigureAwait(false);
                if (discovered is null)
                {
                    return 1;
                }

                identifier = discovered.Identifier;
                name = discovered.Name ?? name;
            }

            await error.WriteLineAsync($"Connecting to {name} ({identifier})...").ConfigureAwait(false);
            device = createDevice(identifier);
            await device.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            logger.PromptConnected();
            await error.WriteLineAsync($"Connected. Armed: {(device.IsArmed ? "yes" : "no")}. Help lists commands.").ConfigureAwait(false);
            exitCode = await createPrompt(device).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.CommandCancelled("prompt");
            exitCode = await CommandError.TryWriteAsync(error, "Session cancelled. Cleaning up.").ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            logger.CommandFailed("prompt", exception.GetType().Name);
            await CommandError.TryWriteAsync(error, $"ERROR prompt: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (device is not null)
            {
                try
                {
                    await device.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    exitCode = 1;
                    logger.CleanupFailed("device", exception.GetType().Name);
                    await CommandError.TryWriteAsync(error, $"ERROR during device cleanup: {exception.Message}").ConfigureAwait(false);
                }
            }
        }

        logger.PromptCompleted(exitCode);
        return exitCode;
    }

    private async Task<DiscoveredDevice?> FindDeviceAsync(CancellationToken cancellationToken)
    {
        await error.WriteLineAsync("Searching for OSSM devices...").ConfigureAwait(false);
        var device = await discovery.FindFirstAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (device is null)
        {
            await error.WriteLineAsync("No devices found.").ConfigureAwait(false);
        }

        return device;
    }
}
