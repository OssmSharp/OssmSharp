using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Simulator;
using OssmSharp.Device.Simulator.Models;
#if WINDOWS
using OssmSharp.Device.Stock;
using Shiny;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli;

public sealed class Program
{
    internal static async Task<int> InvokeAsync(
        CliRootCommand root,
        string[] args,
        CancellationToken cancellationToken)
    {
        var parse = root.Parse(args);
        var exitCode = await parse.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return parse.Errors.Count != 0 ? 2 : cancellationToken.IsCancellationRequested ? 130 : exitCode;
    }

    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        void cancel(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
        Console.CancelKeyPress += cancel;

        try
        {
            var builder = new HostApplicationBuilder();
            builder.Services.AddLogging();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<Func<string, SimulatorBackgroundService>>(
                _ => path => new SimulatorBackgroundService(path));
            builder.Services.AddSingleton<Func<string, SimulatedDevice>>(provider => endpoint =>
                new SimulatedDevice(
                    Options.Create(new SimulatedDeviceOptions { Endpoint = endpoint }),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<SimulatedDevice>>()));

#if WINDOWS
            builder.Services.AddBluetoothLE();
            builder.Services.AddSingleton<DeviceCommand>(provider =>
                new DeviceCommand(provider.GetRequiredService<IBleManager>()));
            builder.Services.AddSingleton<Func<string, string, StockOssmDevice>>(provider =>
                (peripheralId, wifiMac) => new StockOssmDevice(
                    Options.Create(new DeviceRuntimeOptions()),
                    Options.Create(new StockBleOptions
                    {
                        PeripheralId = peripheralId,
                        ExpectedWifiMac = wifiMac,
                    }),
                    provider.GetRequiredService<IBleManager>(),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<StockOssmDevice>>()));
#else
            builder.Services.AddSingleton<DeviceCommand>();
#endif

            builder.Services.AddSingleton<PromptHandler>(provider => new PromptHandler(
                Console.In, Console.Out, Console.Error, DeviceOwnershipLock.TryAcquire,
                provider.GetRequiredService<Func<string, SimulatorBackgroundService>>(),
                provider.GetRequiredService<Func<string, SimulatedDevice>>(),
#if WINDOWS
                (peripheralId, wifiMac) => provider.GetRequiredService<Func<string, string, StockOssmDevice>>()(
                    peripheralId, wifiMac)));
#else
                null));
#endif
            builder.Services.AddSingleton<PromptCommand>();

            builder.Services.AddSingleton<CliRootCommand>();

            using var host = builder.Build();
            var root = host.Services.GetRequiredService<CliRootCommand>();
            return await InvokeAsync(root, args, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
