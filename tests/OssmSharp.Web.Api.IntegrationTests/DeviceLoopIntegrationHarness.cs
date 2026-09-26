namespace OssmSharp.Web.Api.IntegrationTests;

/// <summary>
/// Runs the device loop and push dispatcher against the local simulator for integration tests.
/// </summary>
internal sealed class DeviceLoopIntegrationHarness : IAsyncDisposable
{
    private readonly TaskCompletionSource<DeviceState> _initialState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SimulatorBridgeFixture _bridge;
    private readonly ILoggerFactory _loggerFactory;
    private bool _connectionStarted;
    private int _disposed;

    private DeviceLoopIntegrationHarness(SimulatorBridgeFixture bridge)
    {
        _bridge = bridge;
        Activity = new ControlActivityProvider(TimeProvider.System);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Activity));
        Device = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { Endpoint = bridge.Endpoint }),
            TimeProvider.System,
            _loggerFactory.CreateLogger<SimulatedDevice>());
        Device.ConnectionStateChanged += OnConnectionStateChanged;
        Device.DeviceStateChanged += OnDeviceStateChanged;
        Engine = new ControlEngine(Device, TimeProvider.System);
        Authority = new ControlAuthority(Engine, TimeProvider.System);
        Registry = new ControlHubConnectionRegistry();
        Guests = new GuestControlOperations(Authority);
        Operations = new ControlOperations(
            Authority,
            Options.Create(new ControlServiceOptions
            {
                GuestOrigin = "http://127.0.0.1:5182",
                SimulatorEndpoint = bridge.Endpoint
            }),
            Activity,
            _loggerFactory.CreateLogger<ControlOperations>());
        Sink = new RecordingControlHubPushSink();
        Dispatcher = new ControlPushDispatcher(Operations, Guests, Registry, Activity, Sink,
            _loggerFactory.CreateLogger<ControlPushDispatcher>());
        Loop = new DeviceLoop(
            Authority,
            Dispatcher,
            new ShutdownStepTimeProvider(),
            _loggerFactory.CreateLogger<DeviceLoop>());
        Registry.SubscribeOperator("loop-observer", 0, -1);
    }

    internal ControlActivityProvider Activity { get; }
    internal SimulatedDevice Device { get; }
    internal ControlEngine Engine { get; }
    internal ControlAuthority Authority { get; }
    internal ControlHubConnectionRegistry Registry { get; }
    internal GuestControlOperations Guests { get; }
    internal ControlOperations Operations { get; }
    internal RecordingControlHubPushSink Sink { get; }
    internal ControlPushDispatcher Dispatcher { get; }
    internal DeviceLoop Loop { get; }

    /// <summary>
    /// Waits for the first simulator state before starting the dispatcher and device loop.
    /// </summary>
    /// <returns>The running harness; dispose it to stop its services and device.</returns>
    internal static async Task<DeviceLoopIntegrationHarness> StartAsync(CancellationToken cancellationToken)
    {
        var bridge = new SimulatorBridgeFixture();
        DeviceLoopIntegrationHarness? harness = null;
        try
        {
            await bridge.InitializeAsync();
            harness = new DeviceLoopIntegrationHarness(bridge);
            await harness.Device.StartAsync(cancellationToken);
            await harness._initialState.Task.WaitAsync(cancellationToken);
            await harness.Dispatcher.StartAsync(cancellationToken);
            await harness.Loop.StartAsync(cancellationToken);
            return harness;
        }
        catch
        {
            if (harness is null)
                await bridge.DisposeAsync();
            else
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
            await Authority.DisposeAsync();
            Device.ConnectionStateChanged -= OnConnectionStateChanged;
            Device.DeviceStateChanged -= OnDeviceStateChanged;
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
            try
            {
                Loop.Dispose();
                Dispatcher.Dispose();
                _loggerFactory.Dispose();
                Activity.Dispose();
            }
            finally
            {
                await _bridge.DisposeAsync();
            }
        }
    }

    private void OnConnectionStateChanged(DeviceConnectionState state)
    {
        if (state == DeviceConnectionState.Connecting)
            _connectionStarted = true;
        else if (state == DeviceConnectionState.Disconnected && _connectionStarted)
            _initialState.TrySetException(new IOException("The simulator stopped before publishing initial state."));
    }

    private void OnDeviceStateChanged(OneOf<DeviceState, Exception> change)
    {
        if (change.TryPickT0(out var state, out _) && Device.ConnectionState == DeviceConnectionState.Connected)
            _initialState.TrySetResult(state);
        else if (change.IsT1)
            _initialState.TrySetException(change.AsT1);
    }
}
