#if WINDOWS || MACOS
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OssmSharp.Cli;
using OssmSharp.Cli.Interop;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Stock;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli.IntegrationTests;

#if WINDOWS || MACOS
internal sealed class NativeStockTestComposition : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IDisposable? _platformResources;

    internal NativeStockTestComposition(IStockQualificationResolver? qualificationResolver = null)
    {
        var manager = NativeStockTestBleManagerProxy.Create();
        var peripheral = NativeStockTestPeripheralProxy.Create("selected-stock-device");
        Discovery = new NativeStockTestProvider(
            new StockDeviceIdentifier("selected-stock-device", "Test OSSM", peripheral, manager));
        Transport = new NativeStockTestDevice(Discovery.Selected, initialStroke: 50, initialDepth: 75);
        var services = new ServiceCollection();
        _platformResources = services.AddCliPlatformServices();
        services.AddLogging();
        services.AddSingleton<IBleManager>(manager);
        services.AddSingleton<IDeviceProvider>(Discovery);
        services.AddSingleton<IDeviceTransport>(Transport);
        if (qualificationResolver is not null)
            services.AddSingleton<IStockQualificationResolver>(qualificationResolver);

        try
        {
            _services = services.BuildServiceProvider();
        }
        catch
        {
            _platformResources?.Dispose();
            throw;
        }
    }

    internal NativeStockTestProvider Discovery { get; }

    internal NativeStockTestDevice Transport { get; }

    internal IStockQualificationResolver QualificationResolver =>
        _services.GetRequiredService<IStockQualificationResolver>();

    internal int SimulatorFactoryCalls { get; private set; }

    public void Dispose()
    {
        try
        {
            _services.Dispose();
        }
        finally
        {
            _platformResources?.Dispose();
        }
    }

    internal PromptSession CreatePromptSession(TextReader input, TextWriter output, TextWriter error) =>
        new(
            new PromptHandler(input, output, error),
            _ =>
            {
                SimulatorFactoryCalls++;
                throw new InvalidOperationException("Bluetooth prompt must not create a simulator process.");
            },
            _ =>
            {
                SimulatorFactoryCalls++;
                throw new InvalidOperationException("Bluetooth prompt must not create a simulator device.");
            },
            identifier => _services.GetRequiredService<Func<DeviceIdentifier, DeviceBase>>()(identifier),
            _services.GetRequiredService<IDeviceProvider>(),
            error);

}
#endif
