using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using OneOf;
using OSSMDotNet.Models;

namespace OSSMDotNet;

/// <summary>
/// Provides shared lifecycle, validation, state, and publication behavior for OSSM devices.
/// </summary>
public abstract class DeviceBase : BackgroundService, IOssmDevice, IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _expectationLifetime;
    private readonly ILogger _logger;
    private MotionSettings? _lastRequestedMotion;
    private MotionSettings? _lastStateMotion;
    private int? _lastStateSpeed;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private DeviceInformation? _device;
    private PendingWrite? _pendingWrite;
    private long _matchingContextVersion;
    private CancellationToken _hostCancellationToken;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceBase"/> class.
    /// </summary>
    /// <param name="options">The validated shared device options.</param>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    protected DeviceBase(
        StockOssmDeviceOptions options,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);

        _timeProvider = timeProvider;
        _expectationLifetime = options.ExpectationLifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action<DeviceConnectionState>? ConnectionStateChanged;

    /// <inheritdoc />
    public event Action<OneOf<DeviceState, Exception>>? DeviceStateChanged;

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
            PendingWrite pending;
            lock (_stateGate)
            {
                pending = new PendingWrite(
                    motionSettings,
                    _lastRequestedMotion?.Speed,
                    _lastStateSpeed,
                    _timeProvider.GetTimestamp(),
                    _matchingContextVersion);
                _pendingWrite = pending;
            }

            try
            {
                await DispatchMotionAsync(motionSettings, _hostCancellationToken).ConfigureAwait(false);
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

            lock (_stateGate)
            {
                // A disconnect clears the request history and changes this version number.
                // If that happened while sending, do not put this old request back.
                if (_matchingContextVersion == pending.MatchingContextVersion)
                {
                    _lastRequestedMotion = motionSettings;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _operationGate.Release();
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
    /// Publishes a disconnected connection state.
    /// </summary>
    protected void PublishDisconnected() => SetConnectionState(DeviceConnectionState.Disconnected);

    /// <summary>
    /// Publishes validated device state.
    /// </summary>
    /// <param name="state">The validated device state.</param>
    /// <param name="isOwnWrite">Whether the implementation can directly attribute the state to the pending write.</param>
    protected void PublishState(
        DeviceState state,
        bool? isOwnWrite)
    {
        ArgumentNullException.ThrowIfNull(state);
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(
                state,
                new ValidationContext(state),
                validationResults,
                validateAllProperties: true))
        {
            throw new InvalidDataException(validationResults[0].ErrorMessage);
        }

        UnexpectedSpeedChangeException? unexpectedSpeed = null;
        UnexpectedMotionChangeException? unexpectedSettings = null;
        lock (_stateGate)
        {
            var pending = GetValidPendingWrite();
            var speedExpected = isOwnWrite is true ||
                (isOwnWrite is not false && pending is not null &&
                 IsExpectedState(
                     pending.PreviousRequestedSpeed,
                     pending.PreviousStateSpeed,
                     pending.Settings.Speed,
                     state.MotionSettings.Speed));
            var settingsExpected = pending is not null &&
                isOwnWrite is not false &&
                SameReportedControlSettings(pending.Settings, state.MotionSettings);

            if (speedExpected)
            {
                _pendingWrite = null;
            }
            else if (_lastStateSpeed is { } previousStateSpeed &&
                     previousStateSpeed != state.MotionSettings.Speed)
            {
                unexpectedSpeed = new UnexpectedSpeedChangeException(
                    previousStateSpeed,
                    state.MotionSettings.Speed,
                    pending?.Settings.Speed,
                    state.Timestamp);
            }

            if (!settingsExpected && _lastStateMotion is { } previousSettings &&
                !SameReportedControlSettings(previousSettings, state.MotionSettings))
            {
                unexpectedSettings = new UnexpectedMotionChangeException(
                    previousSettings,
                    state.MotionSettings,
                    pending?.Settings,
                    state.Timestamp);
            }

            _lastStateMotion = state.MotionSettings;
            _lastStateSpeed = state.MotionSettings.Speed;
        }

        Publish(state);
        if (unexpectedSpeed is not null)
        {
            Publish(unexpectedSpeed);
        }
        else if (unexpectedSettings is not null)
        {
            Publish(unexpectedSettings);
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
    /// Determines whether reported state is consistent with a pending request.
    /// </summary>
    /// <param name="previousRequestedSpeed">The requested speed that settled before the pending request was registered.</param>
    /// <param name="previousStateSpeed">The previous reported speed, when available.</param>
    /// <param name="requestedSpeed">The pending requested speed.</param>
    /// <param name="stateSpeed">The newly reported speed.</param>
    /// <returns><see langword="true"/> when the reported speed is consistent with the request.</returns>
    protected virtual bool IsExpectedState(
        int? previousRequestedSpeed,
        int? previousStateSpeed,
        int requestedSpeed,
        int stateSpeed) => requestedSpeed == stateSpeed;

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
        _hostCancellationToken = stoppingToken;
        SetConnectionState(DeviceConnectionState.Connecting);

        try
        {
            await ConnectAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
        {
            PublishDisconnected();
            _logger.ConnectionFailed(exception);
        }
        finally
        {
            PublishDisconnected();
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

    private static bool SameReportedControlSettings(MotionSettings first, MotionSettings second) =>
        first.Stroke == second.Stroke &&
        first.Sensation == second.Sensation &&
        first.Depth == second.Depth &&
        first.Pattern == second.Pattern;

    private PendingWrite? GetValidPendingWrite()
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
        DeviceConnectionState previousState;
        lock (_stateGate)
        {
            _device ??= device;
            if (state == DeviceConnectionState.Disconnected)
            {
                _lastRequestedMotion = null;
                _lastStateMotion = null;
                _lastStateSpeed = null;
                _pendingWrite = null;
                _matchingContextVersion++;
            }

            if (_connectionState == state)
            {
                return;
            }

            previousState = _connectionState;
            _connectionState = state;
        }

        _logger.ConnectionChanged(previousState, state);
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
                _logger.SubscriberFailed(exception);
            }
        }
    }

    /// <summary>
    /// Stores a motion request and the state needed to match later device reports to it.
    /// </summary>
    /// <param name="Settings">The requested motion settings.</param>
    /// <param name="PreviousRequestedSpeed">The speed from the previous completed request, if available.</param>
    /// <param name="PreviousStateSpeed">The most recently reported device speed, if available.</param>
    /// <param name="Timestamp">The monotonic timestamp recorded when the request became pending.</param>
    /// <param name="MatchingContextVersion">The connection context version captured when the request became pending.</param>
    private sealed record PendingWrite(
        MotionSettings Settings,
        int? PreviousRequestedSpeed,
        int? PreviousStateSpeed,
        long Timestamp,
        long MatchingContextVersion);
}
