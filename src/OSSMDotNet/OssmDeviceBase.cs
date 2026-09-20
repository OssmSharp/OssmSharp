using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using OneOf;

namespace OSSMDotNet;

/// <summary>
/// Provides shared lifecycle, validation, observation, and publication behavior for OSSM devices.
/// </summary>
public abstract class OssmDeviceBase : BackgroundService, IOssmDevice, IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _observationLiveFor;
    private readonly TimeSpan _expectationLifetime;
    private readonly ILogger _logger;
    private DeviceState? _state;
    private MotionSettings? _lastRequestedMotion;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private DeviceInformation? _device;
    private long _observationTimestamp;
    private PendingWrite? _pendingWrite;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OssmDeviceBase"/> class.
    /// </summary>
    /// <param name="options">The validated shared device options.</param>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    protected OssmDeviceBase(
        OssmDeviceBaseOptions options,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);

        _timeProvider = timeProvider;
        _observationLiveFor = options.ObservationLiveFor;
        _expectationLifetime = options.ExpectationLifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public DeviceConnectionState ConnectionState
    {
        get
        {
            lock (_stateGate)
            {
                return _connectionState;
            }
        }
    }

    /// <inheritdoc />
    public DeviceInformation Device
    {
        get
        {
            lock (_stateGate)
            {
                return _device ?? throw new InvalidOperationException("Device information has not yet been discovered.");
            }
        }
    }

    /// <inheritdoc />
    public event Action<DeviceConnectionState>? ConnectionStateChanged;

    /// <inheritdoc />
    public event Action<OneOf<DeviceState, Exception>>? DeviceStateChanged;

    /// <inheritdoc />
    public Task<DeviceState> GetStateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            return _state is null
                ? Task.FromException<DeviceState>(new NoDeviceDataException())
                : Task.FromResult(_state);
        }
    }

    /// <inheritdoc />
    public async Task SetMotionAsync(MotionSettings motionSettings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(motionSettings);
        ValidateSettings(motionSettings);
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingWrite(motionSettings, _timeProvider.GetTimestamp());
            lock (_stateGate)
            {
                _pendingWrite = pending;
            }

            try
            {
                await DispatchMotionAsync(motionSettings, StoppingToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_pendingWrite, pending))
                    {
                        _pendingWrite = null;
                    }
                }

                throw;
            }

            DeviceState? state;
            lock (_stateGate)
            {
                _lastRequestedMotion = motionSettings;
                _state = _state is null ? null : _state with { RequestedMotion = motionSettings };
                state = _state;
            }

            if (state is not null)
            {
                Publish(state);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Publishes a connected state with discovered device information.
    /// </summary>
    /// <param name="device">The discovered device information.</param>
    protected void PublishConnected(DeviceInformation device)
    {
        ArgumentNullException.ThrowIfNull(device);
        SetConnectionState(DeviceConnectionState.Connected, device);
    }

    /// <summary>
    /// Publishes a disconnected state without discarding retained observations.
    /// </summary>
    protected void PublishDisconnected() => SetConnectionState(DeviceConnectionState.Disconnected);

    /// <summary>
    /// Commits and publishes a validated observation.
    /// </summary>
    /// <param name="observation">The validated device observation.</param>
    /// <param name="isOwnWrite">Whether the implementation can directly attribute the observation to the pending write.</param>
    protected void PublishObservation(
        DeviceState observation,
        bool? isOwnWrite)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateObservation(observation);

        DeviceState state;
        UnexpectedSpeedChangeException? unexpected = null;
        lock (_stateGate)
        {
            var previous = _state;
            var pending = CurrentPendingWrite();
            var expected = isOwnWrite is true ||
                (isOwnWrite is not false && pending is not null &&
                 IsExpectedObservation(pending.Settings, previous, observation));

            if (expected)
            {
                _pendingWrite = null;
            }
            else if (previous?.MotionSettings is { } previousSettings &&
                     previousSettings.Speed != observation.MotionSettings.Speed)
            {
                unexpected = new UnexpectedSpeedChangeException(
                    previousSettings.Speed,
                    observation.MotionSettings.Speed,
                    pending?.Settings.Speed,
                    observation.Timestamp);
            }

            _observationTimestamp = _timeProvider.GetTimestamp();
            state = observation with
            {
                RequestedMotion = _lastRequestedMotion,
                ObservationFreshness = ObservationFreshness.Live,
            };
            _state = state;
        }

        Publish(state);
        if (unexpected is not null)
        {
            Publish(unexpected);
        }
    }

    /// <summary>
    /// Publishes an asynchronous implementation error without changing connection status.
    /// </summary>
    /// <param name="exception">The semantic exception exposed to consumers.</param>
    protected void PublishError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Publish(exception);
    }

    /// <summary>
    /// Determines whether an observation is consistent with a pending request.
    /// </summary>
    /// <param name="requested">The pending requested settings.</param>
    /// <param name="previous">The previous observation, when available.</param>
    /// <param name="observed">The new observation.</param>
    /// <returns><see langword="true"/> when the observation is consistent with the request.</returns>
    protected virtual bool IsExpectedObservation(
        MotionSettings requested,
        DeviceState? previous,
        DeviceState observed) => requested.Speed == observed.MotionSettings.Speed;

    /// <summary>
    /// Gets the last motion settings whose transport operation settled successfully.
    /// </summary>
    protected MotionSettings? LastRequestedMotion
    {
        get
        {
            lock (_stateGate)
            {
                return _lastRequestedMotion;
            }
        }
    }

    /// <summary>
    /// Runs the implementation-owned transport until shutdown.
    /// </summary>
    protected abstract Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches one validated motion request to the implementation transport.
    /// </summary>
    protected abstract Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases resources owned by the concrete implementation after its transport stops.
    /// </summary>
    /// <returns>Completion after implementation resources are released.</returns>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetConnectionState(DeviceConnectionState.Connecting);

        var freshness = MonitorFreshnessAsync(stoppingToken);
        try
        {
            await ConnectAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            PublishDisconnected();
            PublishError(exception);
        }
        finally
        {
            PublishDisconnected();
            try
            {
                await freshness.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    lock (_stateGate)
                    {
                        _connectionState = DeviceConnectionState.Disconnected;
                    }
                }
                finally
                {
                    _operationGate.Release();
                    _operationGate.Dispose();
                    Dispose();
                    GC.SuppressFinalize(this);
                }
            }
        }
    }

    private CancellationToken StoppingToken { get; set; }

    private async Task MonitorFreshnessAsync(CancellationToken cancellationToken)
    {
        StoppingToken = cancellationToken;
        var interval = TimeSpan.FromTicks(Math.Min(_observationLiveFor.Ticks, TimeSpan.FromMilliseconds(100).Ticks));
        while (true)
        {
            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            DeviceState? changed = null;
            lock (_stateGate)
            {
                _ = CurrentPendingWrite();
                if (_state is { ObservationFreshness: ObservationFreshness.Live } observation &&
                    _timeProvider.GetElapsedTime(_observationTimestamp) >= _observationLiveFor)
                {
                    changed = observation with
                    {
                        ObservationFreshness = ObservationFreshness.LastKnown,
                    };
                    _state = changed;
                }
            }

            if (changed is not null)
            {
                Publish(changed);
            }
        }
    }

    private PendingWrite? CurrentPendingWrite()
    {
        if (_pendingWrite is { } pending &&
            _timeProvider.GetElapsedTime(pending.Timestamp) >= _expectationLifetime)
        {
            _pendingWrite = null;
        }

        return _pendingWrite;
    }

    private void SetConnectionState(DeviceConnectionState state, DeviceInformation? device = null)
    {
        lock (_stateGate)
        {
            _device ??= device;
            if (_connectionState == state)
            {
                return;
            }

            _connectionState = state;
        }

        Publish(ConnectionStateChanged, state);
    }

    private void Publish(OneOf<DeviceState, Exception> change) => Publish(DeviceStateChanged, change);

    private void Publish<T>(Action<T>? handlers, T change)
    {
        if (Volatile.Read(ref _disposed) != 0 || handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(change);
            }
            catch (Exception exception)
            {
                OssmDeviceBaseLog.SubscriberFailed(_logger, exception);
            }
        }
    }

    private static void ValidateObservation(DeviceState observation)
    {
        if (observation.Timestamp is not { Kind: DateTimeKind.Utc })
        {
            throw new InvalidDataException("Observation timestamps must use UTC.");
        }

        if (observation.MotionSettings is null)
        {
            throw new InvalidDataException("A device observation must contain motion settings.");
        }

        try
        {
            ValidateSettings(observation.MotionSettings);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The device observation contains invalid motion settings.", exception);
        }

        if (observation.VelocityMillimetresPerSecond is { } value && !double.IsFinite(value))
        {
            throw new InvalidDataException("The observed velocity must be finite.");
        }
    }

    private static void ValidateSettings(MotionSettings settings)
    {
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(settings, new ValidationContext(settings), validationResults, true))
        {
            throw new ArgumentException(validationResults[0].ErrorMessage, nameof(settings));
        }
    }

    private sealed record PendingWrite(MotionSettings Settings, long Timestamp);
}
