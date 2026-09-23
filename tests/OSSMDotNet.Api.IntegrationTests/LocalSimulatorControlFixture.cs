using System.Threading.Channels;

namespace OSSMDotNet.Api.IntegrationTests;

/// <summary>
/// Runs the local simulator and queues its reported device state for assertions.
/// </summary>
internal sealed class LocalSimulatorControlFixture : IAsyncDisposable
{
    private readonly Channel<OneOf<DeviceState, Exception>> _changes = Channel.CreateBounded<OneOf<DeviceState, Exception>>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<DeviceConnectionState> _connections = Channel.CreateUnbounded<DeviceConnectionState>();
    private readonly ILoggerFactory _loggerFactory;
    private int _disposed;

    private LocalSimulatorControlFixture()
    {
        Activity = new ControlActivityProvider(TimeProvider.System);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Activity));
        Device = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = LocalRustSimulatorPath.Resolve() }),
            TimeProvider.System,
            _loggerFactory.CreateLogger<SimulatedDevice>());
        Device.DeviceStateChanged += change =>
        {
            if (change.IsT0)
                LatestState = change.AsT0;
            _changes.Writer.TryWrite(change);
        };
        Device.ConnectionStateChanged += state => _connections.Writer.TryWrite(state);
        Engine = new PrototypeEngine(Device, TimeProvider.System);
        Authority = new ControlAuthority(Engine, TimeProvider.System);
        Operations = new ControlOperations(
            Authority,
            Options.Create(new ControlServiceOptions
            {
                GuestOrigin = "http://127.0.0.1:5182",
                SimulatorPath = LocalRustSimulatorPath.Resolve()
            }),
            Activity,
            _loggerFactory.CreateLogger<ControlOperations>());
        Faults = new SimulatorFaultOperations(Authority, Device);
    }

    internal ControlActivityProvider Activity { get; }
    internal SimulatedDevice Device { get; }
    internal PrototypeEngine Engine { get; }
    internal ControlAuthority Authority { get; }
    internal ControlOperations Operations { get; }
    internal SimulatorFaultOperations Faults { get; }
    internal DeviceState LatestState { get; private set; } = null!;
    internal bool HasPendingDeviceChanges => _changes.Reader.TryPeek(out _);

    /// <summary>
    /// Starts the simulator and recovers authority after its first connected state.
    /// </summary>
    /// <returns>The started fixture; dispose it to stop the device.</returns>
    internal static async Task<LocalSimulatorControlFixture> StartAsync(CancellationToken cancellationToken)
    {
        var fixture = new LocalSimulatorControlFixture();
        try
        {
            await fixture.Device.StartAsync(cancellationToken);
            await fixture.ReadUntilAsync(
                _ => fixture.Device.ConnectionState == DeviceConnectionState.Connected,
                cancellationToken);
            fixture.Authority.Tick();
            fixture.Operations.Recover(fixture.Operations.Read().Precondition);
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
            var change = await _changes.Reader.ReadAsync(cancellationToken);
            if (change.TryPickT0(out var state, out _) && predicate(state))
                return state;
            if (change.IsT1)
                throw change.AsT1;
        }
    }

    internal async Task<DeviceConnectionState> ReadConnectionUntilAsync(
        Func<DeviceConnectionState, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = await _connections.Reader.ReadAsync(cancellationToken);
            if (predicate(state))
                return state;
        }
    }

    internal void DrainSamples()
    {
        while (_changes.Reader.TryRead(out _))
        {
        }
    }

    internal void DrainConnections()
    {
        while (_connections.Reader.TryRead(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Authority.DisposeAsync();
        }
        finally
        {
            try
            {
                try
                {
                    await Device.StopAsync(CancellationToken.None);
                }
                finally
                {
                    await Device.DisposeAsync();
                }
            }
            finally
            {
                _loggerFactory.Dispose();
                Activity.Dispose();
            }
        }
    }
}
