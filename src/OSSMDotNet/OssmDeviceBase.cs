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
    private DeviceSnapshot _snapshot;
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
        _snapshot = new(
            UtcNow(),
            DeviceConnectionState.Disconnected,
            null,
            null,
            ObservationFreshness.Unavailable,
            null,
            null);
    }

    /// <inheritdoc />
    public event Action<OneOf<DeviceSnapshot, Exception>>? DeviceChanged;

    /// <inheritdoc />
    public Task<DeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            return Task.FromResult(_snapshot);
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

            DeviceSnapshot snapshot;
            lock (_stateGate)
            {
                _snapshot = _snapshot with
                {
                    CapturedAtUtc = UtcNow(),
                    RequestedMotion = motionSettings,
                };
                snapshot = _snapshot;
            }

            Publish(snapshot);
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
        UpdateSnapshot(snapshot => snapshot with
        {
            CapturedAtUtc = UtcNow(),
            ConnectionState = DeviceConnectionState.Connected,
            Device = device,
        });
    }

    /// <summary>
    /// Publishes a disconnected state without discarding retained observations.
    /// </summary>
    protected void PublishDisconnected() => UpdateSnapshot(snapshot => snapshot with
    {
        CapturedAtUtc = UtcNow(),
        ConnectionState = DeviceConnectionState.Disconnected,
    });

    /// <summary>
    /// Commits and publishes a validated observation.
    /// </summary>
    /// <param name="observation">The validated device observation.</param>
    /// <param name="velocityMillimetresPerSecond">Directly observed velocity, when available.</param>
    /// <param name="isOwnWrite">Whether the implementation can directly attribute the observation to the pending write.</param>
    protected void PublishObservation(
        DeviceState observation,
        double? velocityMillimetresPerSecond,
        bool? isOwnWrite)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateObservation(observation, velocityMillimetresPerSecond);

        DeviceSnapshot snapshot;
        UnexpectedSpeedChangeException? unexpected = null;
        lock (_stateGate)
        {
            var previous = _snapshot.Observation;
            var pending = CurrentPendingWrite();
            var expected = isOwnWrite is true ||
                (isOwnWrite is not false && pending is not null &&
                 IsExpectedObservation(pending.Settings, previous, observation));

            if (expected)
            {
                _pendingWrite = null;
            }
            else if (previous is not null &&
                     previous.MotionSettings.Speed != observation.MotionSettings.Speed)
            {
                unexpected = new UnexpectedSpeedChangeException(
                    previous.MotionSettings.Speed,
                    observation.MotionSettings.Speed,
                    pending?.Settings.Speed,
                    observation.Timestamp);
            }

            _observationTimestamp = _timeProvider.GetTimestamp();
            _snapshot = _snapshot with
            {
                CapturedAtUtc = UtcNow(),
                ObservationFreshness = ObservationFreshness.Live,
                Observation = observation,
                ObservedVelocityMillimetresPerSecond = velocityMillimetresPerSecond,
            };
            snapshot = _snapshot;
        }

        Publish(snapshot);
        if (unexpected is not null)
        {
            Publish(unexpected);
        }
    }

    /// <summary>
    /// Publishes an asynchronous implementation error.
    /// </summary>
    /// <param name="exception">The semantic exception exposed to consumers.</param>
    protected void PublishError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        UpdateSnapshot(snapshot => snapshot with
        {
            CapturedAtUtc = UtcNow(),
            ConnectionState = DeviceConnectionState.Faulted,
        });
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
                return _snapshot.RequestedMotion;
            }
        }
    }

    /// <summary>
    /// Runs the implementation-owned transport until shutdown.
    /// </summary>
    protected abstract Task RunTransportAsync(CancellationToken cancellationToken);

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
        UpdateSnapshot(snapshot => snapshot with
        {
            CapturedAtUtc = UtcNow(),
            ConnectionState = DeviceConnectionState.Connecting,
        });

        var freshness = MonitorFreshnessAsync(stoppingToken);
        try
        {
            await RunTransportAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            PublishError(exception);
        }
        finally
        {
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
                        _snapshot = _snapshot with
                        {
                            CapturedAtUtc = UtcNow(),
                            ConnectionState = DeviceConnectionState.Disconnected,
                        };
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
            DeviceSnapshot? changed = null;
            lock (_stateGate)
            {
                _ = CurrentPendingWrite();
                if (_snapshot.ObservationFreshness == ObservationFreshness.Live &&
                    _timeProvider.GetElapsedTime(_observationTimestamp) >= _observationLiveFor)
                {
                    _snapshot = _snapshot with
                    {
                        CapturedAtUtc = UtcNow(),
                        ObservationFreshness = ObservationFreshness.LastKnown,
                    };
                    changed = _snapshot;
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

    private void UpdateSnapshot(Func<DeviceSnapshot, DeviceSnapshot> update)
    {
        DeviceSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = _snapshot = update(_snapshot);
        }

        Publish(snapshot);
    }

    private void Publish(OneOf<DeviceSnapshot, Exception> change)
    {
        if (Volatile.Read(ref _disposed) != 0 || DeviceChanged is not { } handlers)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList().Cast<Action<OneOf<DeviceSnapshot, Exception>>>())
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

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static void ValidateObservation(DeviceState observation, double? velocity)
    {
        if (observation.Timestamp.Kind != DateTimeKind.Utc)
        {
            throw new InvalidDataException("Observation timestamps must use UTC.");
        }

        try
        {
            ValidateSettings(observation.MotionSettings);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The device observation contains invalid motion settings.", exception);
        }

        if (velocity is { } value && !double.IsFinite(value))
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
