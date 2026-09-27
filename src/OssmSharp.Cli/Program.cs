using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Simulator;
using OssmSharp.Device.Simulator.Models;
#if MACOS
using CoreFoundation;
#endif
#if WINDOWS || MACOS
using OssmSharp.Device.Stock;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli;

/// <summary>
/// Composes the foreground CLI and invokes its selected command.
/// </summary>
public sealed class Program
{
    internal static async Task<int> InvokeAsync(
        CliRootCommand root,
        string[] args,
        CancellationToken cancellationToken)
    {
        // The foreground CLI has no GUI message loop to run captured command continuations.
        SynchronizationContext.SetSynchronizationContext(null);

        var parse = root.Parse(args);
        try
        {
            var configuration = new InvocationConfiguration
            {
                // Signal handling must outlive the sequential prompt, Stop, device and child cleanup.
                ProcessTerminationTimeout = TimeSpan.FromSeconds(30),
            };
            var exitCode = await parse.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
            return parse.Errors.Count != 0
                ? 2
                : exitCode != 0
                    ? exitCode
                    : cancellationToken.IsCancellationRequested ? 130 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
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
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<Func<string, SimulatorBackgroundService>>(
                _ => path => new SimulatorBackgroundService(path));
            builder.Services.AddSingleton<Func<string, SimulatedDevice>>(provider => endpoint =>
            {
                var options = new SimulatedDeviceOptions { Endpoint = endpoint };
                Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
                return new SimulatedDevice(
                    Options.Create(options),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<SimulatedDevice>>());
            });

#if MACOS
#pragma warning disable XCODE_27_0_PREVIEW
            using var dispatchQueue = new DispatchQueue("org.ossm.devicecli");
#pragma warning restore XCODE_27_0_PREVIEW
            builder.Services.AddStockBluetooth(dispatchQueue);
#elif WINDOWS
            builder.Services.AddStockBluetooth();
#endif
#if WINDOWS || MACOS
            builder.Services.AddSingleton<Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>>>(
                provider => cancellationToken => StockBleDiscovery.ScanAsync(
                    provider.GetRequiredService<IBleManager>(), cancellationToken));
            builder.Services.AddSingleton<StockIdentityReader>(provider =>
                new StockIdentityReader(provider.GetRequiredService<IBleManager>()));
            builder.Services.AddSingleton<Func<string, StockOssmDevice>>(provider =>
                peripheralId => new StockOssmDevice(
                    Options.Create(new DeviceRuntimeOptions()),
                    Options.Create(new StockBleOptions { PeripheralId = peripheralId }),
                    provider.GetRequiredService<IBleManager>(),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<StockOssmDevice>>()));
#endif
            builder.Services.AddSingleton<DeviceListCommand>(provider =>
                new DeviceListCommand(
#if WINDOWS || MACOS
                    provider.GetRequiredService<Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>>>(),
#else
                    _ => Task.FromException<IReadOnlyList<(string Name, string Id)>>(
                        new PlatformNotSupportedException("Stock BLE discovery requires a native Windows or macOS build.")),
#endif
                    Console.Out,
                    Console.Error));
            builder.Services.AddSingleton<DeviceGetCommand>(provider =>
#if WINDOWS || MACOS
                new DeviceGetCommand(
                    (id, cancellationToken) =>
                        provider.GetRequiredService<StockIdentityReader>().ReadAsync(id, cancellationToken),
                    Console.Out,
                    Console.Error));
#else
                new DeviceGetCommand(Console.Out, Console.Error));
#endif
            builder.Services.AddSingleton<DeviceCommand>(provider =>
                new DeviceCommand(
                    provider.GetRequiredService<DeviceGetCommand>(),
                    provider.GetRequiredService<DeviceListCommand>()));

            builder.Services.AddSingleton<PromptHandler>(_ =>
                new PromptHandler(new ForegroundInputReader(Console.In), Console.Out, Console.Error));
            builder.Services.AddSingleton<PromptSession>(provider =>
                new PromptSession(
                    provider.GetRequiredService<PromptHandler>(),
                    provider.GetRequiredService<Func<string, SimulatorBackgroundService>>(),
                    provider.GetRequiredService<Func<string, SimulatedDevice>>(),
#if WINDOWS || MACOS
                    provider.GetRequiredService<Func<string, StockOssmDevice>>(),
                    provider.GetRequiredService<Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>>>(),
#else
                    null,
                    null,
#endif
                    Console.Error));
            builder.Services.AddSingleton<PromptCommand>(provider =>
                new PromptCommand(provider.GetRequiredService<PromptSession>()));
            builder.Services.AddSingleton<CliRootCommand>(provider =>
                new CliRootCommand(
                    provider.GetRequiredService<DeviceCommand>(),
                    provider.GetRequiredService<PromptCommand>()));

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
