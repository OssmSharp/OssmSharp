using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

internal sealed class DeviceLoopIntegrationHarness : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private int _disposed;

    private DeviceLoopIntegrationHarness()
    {
        Activity = new ControlActivityProvider(TimeProvider.System);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Activity));
        Bridge = new SimulatorProcessTransport(_loggerFactory.CreateLogger<SimulatorProcessTransport>());
        Engine = new PrototypeEngine(Bridge, TimeProvider.System);
        Authority = new ControlAuthority(Engine);
        Registry = new ControlHubConnectionRegistry();
        Guests = new GuestControlOperations(Authority, Registry);
        Operations = new ControlOperations(
            Authority,
            Bridge,
            Options.Create(new ControlServiceOptions
            {
                GuestOrigin = "http://127.0.0.1:5182",
                SimulatorPath = LocalRustSimulatorPath.Resolve()
            }),
            Activity,
            _loggerFactory.CreateLogger<ControlOperations>());
        Sink = new RecordingControlHubPushSink();
        Dispatcher = new ControlPushDispatcher(Operations, Guests, Registry, Activity, Sink,
            _loggerFactory.CreateLogger<ControlPushDispatcher>());
        Loop = new DeviceLoop(
            Authority,
            Bridge,
            Dispatcher,
            Options.Create(new ControlServiceOptions
            {
                GuestOrigin = "http://127.0.0.1:5182",
                SimulatorPath = LocalRustSimulatorPath.Resolve()
            }),
            new ShutdownStepTimeProvider(),
            _loggerFactory.CreateLogger<DeviceLoop>());
        Registry.SubscribeOperator("loop-observer", 0, -1);
    }

    internal ControlActivityProvider Activity { get; }
    internal SimulatorProcessTransport Bridge { get; }
    internal PrototypeEngine Engine { get; }
    internal ControlAuthority Authority { get; }
    internal ControlHubConnectionRegistry Registry { get; }
    internal GuestControlOperations Guests { get; }
    internal ControlOperations Operations { get; }
    internal RecordingControlHubPushSink Sink { get; }
    internal ControlPushDispatcher Dispatcher { get; }
    internal DeviceLoop Loop { get; }

    internal static async Task<DeviceLoopIntegrationHarness> StartAsync(CancellationToken cancellationToken)
    {
        var harness = new DeviceLoopIntegrationHarness();
        try
        {
            await harness.Dispatcher.StartAsync(cancellationToken);
            await harness.Loop.StartAsync(cancellationToken);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Loop.StopAsync(CancellationToken.None);
            await Dispatcher.StopAsync(CancellationToken.None);
            await Engine.DisposeAsync();
            await Bridge.DisposeAsync();
        }
        finally
        {
            Loop.Dispose();
            Dispatcher.Dispose();
            _loggerFactory.Dispose();
            Activity.Dispose();
        }
    }
}
