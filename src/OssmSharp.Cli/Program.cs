using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Cli.Interop;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Simulator;
using OssmSharp.Device.Simulator.Models;

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
            using var platformResources = builder.Services.AddCliPlatformServices();
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
            RegisterDeviceCommands(builder.Services);
            RegisterPromptCommands(builder.Services);

            using var host = builder.Build();
            var root = host.Services.GetRequiredService<CliRootCommand>();
            return await InvokeAsync(root, args, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static void RegisterDeviceCommands(IServiceCollection services)
    {
        services.AddSingleton<DeviceListCommand>(provider =>
            new DeviceListCommand(
                provider.GetRequiredService<IDeviceProvider>(),
                Console.Out,
                Console.Error));
        services.AddSingleton<DeviceGetCommand>(provider =>
            new DeviceGetCommand(
                provider.GetRequiredService<IDeviceProvider>(),
                Console.Out,
                Console.Error));
        services.AddSingleton<DeviceCommand>(provider =>
            new DeviceCommand(
                provider.GetRequiredService<DeviceGetCommand>(),
                provider.GetRequiredService<DeviceListCommand>()));
    }

    private static void RegisterPromptCommands(IServiceCollection services)
    {
        services.AddSingleton<PromptHandler>(_ =>
            new PromptHandler(new ForegroundInputReader(Console.In), Console.Out, Console.Error));
        services.AddSingleton<PromptSession>(provider =>
            new PromptSession(
                provider.GetRequiredService<PromptHandler>(),
                provider.GetRequiredService<Func<string, SimulatorBackgroundService>>(),
                provider.GetRequiredService<Func<string, SimulatedDevice>>(),
                provider.GetService<Func<DeviceIdentifier, IOssmDevice>>(),
                provider.GetRequiredService<IDeviceProvider>(),
                provider.GetRequiredService<TimeProvider>(),
                Console.Error));
        services.AddSingleton<PromptCommand>(provider =>
            new PromptCommand(provider.GetRequiredService<PromptSession>()));
        services.AddSingleton<CliRootCommand>(provider =>
            new CliRootCommand(
                provider.GetRequiredService<DeviceCommand>(),
                provider.GetRequiredService<PromptCommand>()));
    }
}
