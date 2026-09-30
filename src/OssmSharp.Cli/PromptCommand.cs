using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

public sealed class PromptCommand
{
    private readonly IDeviceDiscovery discovery;
    private readonly Func<string, IOssmDevice> createDevice;
    private readonly Func<IOssmDevice, IPromptHandler> createPrompt;
    private readonly TextWriter error;
    private readonly ILogger<PromptCommand> logger;

    internal PromptCommand(
        IDeviceDiscovery discovery,
        Func<string, IOssmDevice> createDevice,
        Func<IOssmDevice, IPromptHandler> createPrompt,
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
        var operation = identifier is null ? "discovery" : "prompt";
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

            operation = "prompt";
            await error.WriteLineAsync(CliStrings.Connecting(name, identifier)).ConfigureAwait(false);
            device = createDevice(identifier);
            await device.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            logger.PromptConnected();
            await error.WriteLineAsync(CliStrings.Connected).ConfigureAwait(false);
            exitCode = await createPrompt(device).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.CommandCancelled("prompt");
            exitCode = await CommandError.TryWriteAsync(error, CliStrings.Cancellation).ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            logger.CommandFailed("prompt", exception.GetType().Name);
            var detail = operation == "discovery" ? CliStrings.DiscoveryFailure(exception.Message) : exception.Message;
            await CommandError.TryWriteAsync(error, CliStrings.Error(operation, detail)).ConfigureAwait(false);
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
                    await CommandError.TryWriteAsync(error, CliStrings.Error("device cleanup", exception.Message)).ConfigureAwait(false);
                }
            }
        }

        logger.PromptCompleted(exitCode);
        return exitCode;
    }

    private async Task<DiscoveredDevice?> FindDeviceAsync(CancellationToken cancellationToken)
    {
        await error.WriteLineAsync(CliStrings.DiscoveryProgress).ConfigureAwait(false);
        var device = await discovery.FindFirstAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (device is null)
        {
            await error.WriteLineAsync(CliStrings.NoDevices).ConfigureAwait(false);
        }

        return device;
    }
}
