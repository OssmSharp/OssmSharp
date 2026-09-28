using System.CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

internal static class Program
{
    internal static Task<int> Main(string[] args) => InvokeAsync(args, Console.Out, Console.Error);

    internal static async Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error)
    {
        var root = new RootCommand("Discover OEM OSSM devices or open a terminal prompt. Connection never arms or sends settings.");
        var list = new Command("list", "List discovered device identifiers and names without connecting.");
        list.SetAction((_, token) => RunDeviceCommandAsync(false, null, output, error, token));
        root.Subcommands.Add(list);

        var prompt = new Command("prompt", "Connect to the selected or sole discovered device. Requires terminal stdin.");
        var device = new Option<string?>("--device")
        {
            Description = "Device identifier from OssmSharp.Cli list; otherwise select the sole discovered device.",
            Arity = ArgumentArity.ExactlyOne,
        };
        device.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(result.GetValueOrDefault<string?>()))
            {
                result.AddError("--device requires a nonempty identifier from OssmSharp.Cli list.");
            }
        });
        prompt.Options.Add(device);
        prompt.SetAction((parsed, token) => RunDeviceCommandAsync(true, parsed.GetValue(device), output, error, token));
        root.Subcommands.Add(prompt);

        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
        };

        try
        {
            var parsed = root.Parse(args);
            if (parsed.Errors.Count > 0)
            {
                foreach (var issue in parsed.Errors)
                {
                    await error.WriteLineAsync($"ERROR: {issue.Message}").ConfigureAwait(false);
                }

                await error.WriteLineAsync("Run OssmSharp.Cli --help for usage.").ConfigureAwait(false);
                return 2;
            }

            return await parsed.InvokeAsync(configuration).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CommandError.TryWriteAsync(error, $"ERROR: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> ListAsync(
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> scan,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await error.WriteLineAsync("Searching for OSSM devices ...").ConfigureAwait(false);
            var devices = await scan(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var device in devices)
            {
                await output.WriteLineAsync($"{device.Identifier}  {device.Name}").ConfigureAwait(false);
            }

            if (devices.Count == 0)
            {
                await error.WriteLineAsync("No devices found.").ConfigureAwait(false);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CommandError.TryWriteAsync(error, "Discovery cancelled.").ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            await CommandError.TryWriteAsync(error, $"ERROR discovery: {exception.Message} Check Bluetooth access and retry OssmSharp.Cli list.").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> PromptAsync(
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> scan,
        Func<string, IOssmDevice> factory,
        string? identifier,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool interactive = false,
        CancellationToken cancellationToken = default)
    {
        IOssmDevice? device = null;
        var exitCode = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await error.WriteLineAsync("Searching for OSSM devices...").ConfigureAwait(false);
            var devices = await scan(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            DiscoveredDevice? selected;
            if (identifier is not null)
            {
                selected = devices.FirstOrDefault(candidate => string.Equals(candidate.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    await error.WriteLineAsync("ERROR selection: selected device is unavailable. No connection made. Run OssmSharp.Cli list and retry with --device <id>.").ConfigureAwait(false);
                    return 1;
                }
            }
            else
            {
                if (devices.Count == 0)
                {
                    await error.WriteLineAsync("No OSSM devices found.").ConfigureAwait(false);
                    return 1;
                }

                if (devices.Count != 1)
                {
                    await error.WriteLineAsync($"Found {devices.Count} devices. Run OssmSharp.Cli list, then OssmSharp.Cli prompt --device <id>.").ConfigureAwait(false);
                    return 1;
                }

                selected = devices[0];
            }

            await error.WriteLineAsync($"Connecting to {selected.Name ?? "OSSM"} ({selected.Identifier})...").ConfigureAwait(false);
            device = factory(selected.Identifier);
            await device.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await error.WriteLineAsync($"Connected. Armed: {(device.IsArmed ? "yes" : "no")}. Enter reset to enable commands; help lists commands.").ConfigureAwait(false);
            exitCode = await new PromptHandler(device, input, output, error, interactive).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitCode = await CommandError.TryWriteAsync(error, "Session cancelled; awaiting device cleanup.").ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            await CommandError.TryWriteAsync(error, $"ERROR prompt: {exception.Message} Run OssmSharp.Cli list before reconnecting.").ConfigureAwait(false);
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
                    await CommandError.TryWriteAsync(error, $"ERROR device cleanup: {exception.Message}").ConfigureAwait(false);
                }
            }
        }

        return exitCode;
    }

    private static async Task<int> RunDeviceCommandAsync(bool prompt, string? identifier, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (prompt && Console.IsInputRedirected)
        {
            await error.WriteLineAsync("ERROR input: prompt requires terminal stdin. Run OssmSharp.Cli prompt in a terminal without piping or redirecting stdin; stdout and stderr may be redirected.").ConfigureAwait(false);
            return 1;
        }

        IHost? host = null;
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
            host = builder.Build();
            stoppingToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
            await host.StartAsync(operation.Token).ConfigureAwait(false);
            var discovery = host.Services.GetRequiredService<DeviceDiscovery>();
            if (prompt)
            {
                var factory = host.Services.GetRequiredService<Func<string, DeviceBase>>();
                using var input = new ConsoleInputReader();
                exitCode = await PromptAsync(discovery.ScanAsync, factory, identifier, input, output, error, true, operation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                exitCode = await ListAsync(discovery.ScanAsync, output, error, operation.Token).ConfigureAwait(false);
            }
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
        }

        return exitCode;
    }
}
