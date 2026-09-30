using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

internal static class HostRunner
{
    internal static async Task<int> RunAsync(
        Func<IServiceProvider, CancellationToken, Task<int>> command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IHost? host = null;
        TextWriter? originalError = null;
        var stoppingToken = CancellationToken.None;
        var exitCode = 0;
#if MACOS
        using var bluetoothQueue = new CoreFoundation.DispatchQueue("OssmSharp.Cli.Bluetooth");
#endif
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
#if MACOS
            Shiny.BleServiceCollectionExtensions.AddBluetoothLE(
                builder.Services,
                new Shiny.BluetoothLE.AppleBleConfiguration(DispatchQueue: bluetoothQueue));
#endif
            builder.Services.AddOemRuntime();
            var console = PromptConsole.FromConsole(output, error);
            builder.Services.AddSingleton(console);
            builder.Services.AddCliCommands(output, error);
            if (ReferenceEquals(error, Console.Error))
            {
                originalError = Console.Error;
                Console.SetError(console.Error);
            }

            host = builder.Build();
            stoppingToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
            await host.StartAsync(operation.Token).ConfigureAwait(false);
            exitCode = await command(host.Services, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
            exitCode = await CommandError.TryWriteAsync(error, "Operation cancelled; awaiting host cleanup.").ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            await CommandError.TryWriteAsync(error, $"ERROR: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (host is not null)
            {
                try
                {
                    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    exitCode = 1;
                    await CommandError.TryWriteAsync(error, $"ERROR host stop: {exception.Message}").ConfigureAwait(false);
                }

                try
                {
                    if (host is IAsyncDisposable asyncHost)
                    {
                        await asyncHost.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        host.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    exitCode = 1;
                    await CommandError.TryWriteAsync(error, $"ERROR host cleanup: {exception.Message}").ConfigureAwait(false);
                }
            }

            if (originalError is not null)
            {
                Console.SetError(originalError);
            }
        }

        return exitCode;
    }
}
