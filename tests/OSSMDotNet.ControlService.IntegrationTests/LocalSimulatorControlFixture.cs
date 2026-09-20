using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

internal sealed class LocalSimulatorControlFixture : IAsyncDisposable
{
    private readonly Channel<DeviceState> _samples = Channel.CreateBounded<DeviceState>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ILoggerFactory _loggerFactory;
    private int _disposed;

    private LocalSimulatorControlFixture()
    {
        Activity = new ControlActivityProvider(TimeProvider.System);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Activity));
        Bridge = new SimulatorProcessTransport(_loggerFactory.CreateLogger<SimulatorProcessTransport>());
        Bridge.SampleReceived += sample => _samples.Writer.TryWrite(sample);
        Engine = new PrototypeEngine(Bridge, TimeProvider.System);
        Authority = new ControlAuthority(Engine);
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
    }

    internal ControlActivityProvider Activity { get; }
    internal SimulatorProcessTransport Bridge { get; }
    internal PrototypeEngine Engine { get; }
    internal ControlAuthority Authority { get; }
    internal ControlOperations Operations { get; }

    internal static async Task<LocalSimulatorControlFixture> StartAsync(CancellationToken cancellationToken)
    {
        var fixture = new LocalSimulatorControlFixture();
        try
        {
            fixture.Bridge.Start(LocalRustSimulatorPath.Resolve());
            await fixture.ReadUntilAsync(sample => sample.Connected, cancellationToken);
            fixture.Authority.Tick();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal async Task<DeviceState> ReadUntilAsync(
        Func<DeviceState, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var sample = await _samples.Reader.ReadAsync(cancellationToken);
            if (predicate(sample))
                return sample;
        }
    }

    internal void DrainSamples()
    {
        while (_samples.Reader.TryRead(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Engine.DisposeAsync();
        }
        finally
        {
            try
            {
                await Bridge.DisposeAsync();
            }
            finally
            {
                _loggerFactory.Dispose();
                Activity.Dispose();
            }
        }
    }
}
