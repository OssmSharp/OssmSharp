using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using OneOf;

namespace OssmSharp.Device.Runtime;

/// <summary>
/// Serializes device commands, publishes state and disables motion after a Stop or external change.
/// </summary>
/// <remarks>
/// <para>
/// A new command supersedes pending commands and the unissued remainder of the active command. Issued I/O settles
/// before the next command starts. An emergency Stop cannot be superseded; it writes speed zero only and must
/// finish before initialization can reset readiness.
/// </para>
/// <para>
/// Providers implement the transport through <see cref="ConnectAsync"/> and the protected dispatch, preparation and
/// validation hooks. <see cref="BackgroundService.StopAsync(CancellationToken)"/> ends the transport; it does not write
/// a Stop.
/// </para>
/// </remarks>
public abstract class DeviceBase : BackgroundService, IOssmDevice
{
    private static readonly TimeSpan DisposalBound = TimeSpan.FromSeconds(5);
    private readonly object _stateGate = new();
    private readonly object _disposalGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly List<PendingWrite> _pendingWrites = [];
    private CancellationTokenSource _motionRevocation = new();
    private Task _stopTask = Task.CompletedTask;
    private int? _lastRequestedSpeed;
    private DeviceState? _latestState;
    private bool _ready;
    private long _stopCount;
    private int _closing;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private DeviceInformation? _device;
    private PendingWrite? _pendingWrite;
    private CancellationToken _hostCancellationToken;
    private int _disposed;
    private Task? _disposalTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceBase"/> class.
    /// </summary>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    protected DeviceBase(
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
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
    public DeviceState? LatestState
    {
        get
        {
            lock (_stateGate)
            {
                return _latestState;
            }
        }
    }

    /// <inheritdoc />
    public bool IsReady
    {
        get
        {
            lock (_stateGate)
            {
                return IsReadyCore();
            }
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(null, cancellationToken);

    /// <inheritdoc />
    public Task SetAsync(MotionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);
        return RunCommandAsync(settings, cancellationToken);
    }

    /// <inheritdoc />
    public Task EmergencyStopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        CancellationTokenSource revoked;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0 || _closing != 0, this);
            revoked = DisableCore();
            stop = StartStopCore();
        }

        CancelAndDispose(revoked);
        return stop.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            var disposal = _disposalTask ??= DisposeDeviceAsync();
            GC.SuppressFinalize(this);
            return new ValueTask(disposal);
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

        UnexpectedMotionChangeException? unexpectedMotion = null;
        InvalidDataException? leftPlayReady = null;
        lock (_stateGate)
        {
            unexpectedMotion = AttributeReport(state, isOwnWrite);
            _latestState = state;
            if (unexpectedMotion is null && _ready && state.MotionSettings.State != MotionDeviceState.StrokeEngineIdle)
                leftPlayReady = new InvalidDataException("The device left its verified play-ready state.");
            if (unexpectedMotion is not null || leftPlayReady is not null)
                _ready = false;
        }

        var fault = (Exception?)unexpectedMotion ?? leftPlayReady;
        if (fault is not null)
            StopForFault();

        Publish(state);
        if (fault is not null)
            Publish(fault);
    }

    /// <summary>
    /// Publishes an asynchronous device error without changing connection status, then stops the device.
    /// </summary>
    /// <param name="exception">The semantic exception exposed to consumers.</param>
    protected void PublishError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StopForFault();
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
    /// <param name="motionSettings">The validated tuple to write.</param>
    /// <param name="requestCancellationToken">
    /// Cancelled by the caller, a newer command or a Stop; check it before each write, not while one is in flight.
    /// </param>
    /// <param name="shutdownToken">Cancelled only when the device shuts down; use it to wait for issued I/O.</param>
    /// <returns>Completion after the issued write settles.</returns>
    protected abstract Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken);

    /// <summary>
    /// Dispatches a Stop that writes speed zero only, without writing or qualifying any other setting.
    /// </summary>
    /// <remarks>
    /// It must work before the device has reported any settings.
    /// </remarks>
    /// <param name="requestCancellationToken">Cancelled by the caller or an emergency stop; check it before issuing work.</param>
    /// <param name="shutdownToken">Cancelled only when the device shuts down; use it to wait for issued I/O.</param>
    /// <returns>Completion after the zero-speed write settles.</returns>
    /// <exception cref="IOException">The device is not connected.</exception>
    protected abstract Task DispatchSafetyStopAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken);

    /// <summary>
    /// Performs provider readiness preparation after initialization has written zero speed.
    /// </summary>
    /// <remarks>
    /// Preparation must not navigate or start motion. An issued operation must settle using
    /// <paramref name="shutdownToken"/> even after <paramref name="requestCancellationToken"/> is cancelled. The default
    /// does nothing.
    /// </remarks>
    /// <param name="requestCancellationToken">Cancelled by the caller or an emergency stop; check it before issuing work.</param>
    /// <param name="shutdownToken">Cancelled only when the device shuts down; use it to wait for issued I/O.</param>
    /// <returns>Completion after preparation settles.</returns>
    protected virtual Task PrepareAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Rejects settings the provider cannot write, before any of them is written.
    /// </summary>
    /// <remarks>
    /// Called once, without I/O, when a <see cref="SetAsync"/> command runs. The default accepts every validated tuple.
    /// </remarks>
    /// <param name="settings">The complete requested settings.</param>
    /// <exception cref="InvalidOperationException">The provider cannot write the settings.</exception>
    protected virtual void ValidateMotion(MotionSettings settings)
    {
    }

    /// <summary>Records that at least one write has been issued and may settle after a later failure.</summary>
    protected void MarkWriteIssued()
    {
        lock (_stateGate)
        {
            if (_pendingWrite is { } pending)
            {
                pending.Issued = true;
            }
        }
    }

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
        first with { State = second.State, Speed = second.Speed, Position = second.Position } == second;

    private static bool HasOnlyExpectedControlChanges(
        MotionSettings? previous,
        MotionSettings requested,
        MotionSettings reported) =>
        previous is not null
        && (reported.Stroke == previous.Stroke || reported.Stroke == requested.Stroke)
        && (reported.Sensation == previous.Sensation || reported.Sensation == requested.Sensation)
        && (reported.Depth == previous.Depth || reported.Depth == requested.Depth)
        && (reported.Buffer == previous.Buffer || reported.Buffer == requested.Buffer)
        && (reported.Pattern == previous.Pattern || reported.Pattern == requested.Pattern);

    // Reports can arrive after later commands have already been written. Retire earlier expectations when
    // ordered feedback reaches a later write; elapsed time alone never makes a change-only report invalid.
    private UnexpectedMotionChangeException? AttributeReport(DeviceState state, bool? isOwnWrite)
    {
        var reported = state.MotionSettings;
        var previous = _latestState?.MotionSettings;
        if (isOwnWrite is not false)
        {
            for (var index = 0; index < _pendingWrites.Count; index++)
            {
                var pending = _pendingWrites[index];
                var requested = pending.Settings;
                var speedMatches = isOwnWrite is true
                    || (requested is null
                        ? reported.Speed == 0
                        : IsExpectedState(pending.PreviousRequestedSpeed, pending.PreviousStateSpeed,
                            requested.Speed, reported.Speed));
                var settingsMatch = requested is null
                    ? previous is null || SameReportedControlSettings(previous, reported)
                    : HasOnlyExpectedControlChanges(pending.PreviousStateMotion, requested, reported);
                if (!settingsMatch || (!speedMatches && previous?.Speed != reported.Speed))
                    continue;

                if (speedMatches && (requested is null || SameReportedControlSettings(requested, reported)))
                {
                    pending.Reported = true;
                    _pendingWrites.RemoveRange(0, index + 1);
                }

                return null;
            }
        }

        return previous is not null
            && (previous.Speed != reported.Speed || !SameReportedControlSettings(previous, reported))
            ? new UnexpectedMotionChangeException(previous, reported, _pendingWrites.LastOrDefault()?.Settings, state.Timestamp)
            : null;
    }

    private async Task RunCommandAsync(MotionSettings? settings, CancellationToken cancellationToken)
    {
        CancellationTokenSource revoked;
        CancellationTokenSource cancellation;
        Task stop;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0 || _closing != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            revoked = _motionRevocation;
            _motionRevocation = new CancellationTokenSource();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_motionRevocation.Token, cancellationToken);
            stop = _stopTask;
        }

        CancelAndDispose(revoked);
        using var ownedCancellation = cancellation;
        cancellationToken = cancellation.Token;
        await stop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settings is null)
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            else
                await SetCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        long stops;
        lock (_stateGate)
        {
            if (_connectionState != DeviceConnectionState.Connected
                || _latestState?.MotionSettings is not { State: MotionDeviceState.StrokeEngineIdle, Speed: 0 })
                throw new InvalidOperationException("The last known state must show the device stopped in its play-ready state.");
            _ready = false;
            stops = _stopCount;
        }

        var wrote = false;
        try
        {
            await WriteCoreAsync(null, cancellationToken).ConfigureAwait(false);
            wrote = true;
            await PrepareAsync(cancellationToken, _hostCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StopAfterFailedWrite(wrote, exception))
        {
            throw;
        }

        lock (_stateGate)
        {
            // An emergency stop, fault or disconnect during initialization keeps the device not ready.
            if (_stopCount != stops)
                throw new OperationCanceledException("The device stopped during initialization.");
            cancellationToken.ThrowIfCancellationRequested();
            _ready = true;
        }
    }

    private async Task SetCoreAsync(MotionSettings settings, CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (!IsReadyCore())
                throw new InvalidOperationException("Device is not ready for motion.");
            if (!_device!.SupportedPatterns.Contains(settings.Pattern))
                throw new InvalidOperationException("The device does not support the requested pattern.");
            ValidateMotion(settings);
        }

        try
        {
            await WriteCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StopAfterFailedWrite(wrote: false, exception))
        {
            throw;
        }
    }

    // A failure after a write, or a transport failure during one, leaves the device state unknown, so the
    // device is stopped. A rejection before the first write and cancellation do not stop it. Always returns
    // false so that it can observe the exception from a filter.
    private bool StopAfterFailedWrite(bool wrote, Exception exception)
    {
        if (exception is not OperationCanceledException
            && (wrote || exception is not (InvalidOperationException or ArgumentException or NotSupportedException)))
            StopForFault();
        return false;
    }

    private bool IsReadyCore() =>
        _ready
        && _connectionState == DeviceConnectionState.Connected
        && _latestState?.MotionSettings.State == MotionDeviceState.StrokeEngineIdle;

    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private CancellationTokenSource DisableCore()
    {
        _ready = false;
        _stopCount++;
        var revoked = _motionRevocation;
        _motionRevocation = new CancellationTokenSource();
        return revoked;
    }

    private void StopForFault()
    {
        CancellationTokenSource revoked;
        lock (_stateGate)
        {
            if (_disposed != 0 || _closing != 0)
                return;
            revoked = DisableCore();
            if (_connectionState == DeviceConnectionState.Connected)
                StartStopCore();
        }

        CancelAndDispose(revoked);
    }

    private Task StartStopCore() =>
        !_stopTask.IsCompleted ? _stopTask : _stopTask = Task.Run(WriteStopAsync, CancellationToken.None);

    private async Task WriteStopAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.EmergencyStopFailed(exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task WriteCoreAsync(
        MotionSettings? motionSettings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        PendingWrite pending;
        lock (_stateGate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
                throw new IOException("The device is not connected, so no command could be written.");
            pending = new PendingWrite(
                motionSettings,
                _lastRequestedSpeed,
                _latestState?.MotionSettings.Speed,
                _pendingWrites.LastOrDefault()?.Settings ?? _latestState?.MotionSettings);
            _pendingWrite = pending;
            _pendingWrites.Add(pending);
        }

        try
        {
            if (motionSettings is null)
                await DispatchSafetyStopAsync(cancellationToken, _hostCancellationToken).ConfigureAwait(false);
            else
                await DispatchMotionAsync(motionSettings, cancellationToken, _hostCancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateGate)
            {
                if (!pending.Issued)
                    _pendingWrites.Remove(pending);
            }

            throw;
        }

        lock (_stateGate)
        {
            if (ReferenceEquals(_pendingWrite, pending))
                _lastRequestedSpeed = motionSettings?.Speed ?? 0;
        }
    }

    private async Task DisposeDeviceAsync()
    {
        // Stop motion before writes close. A Stop that does not settle leaves issued I/O possibly
        // running, so the transport stays owned rather than being released underneath it.
        bool stop;
        Task stopping;
        CancellationTokenSource revoked;
        Volatile.Write(ref _closing, 1);
        lock (_stateGate)
        {
            stop = _connectionState == DeviceConnectionState.Connected
                && (_ready || _lastRequestedSpeed > 0 || _latestState?.MotionSettings.Speed > 0);
            revoked = DisableCore();
            stopping = stop ? StartStopCore() : _stopTask;
        }

        CancelAndDispose(revoked);
        Exception? stopFailure = null;
        if (stop || !stopping.IsCompletedSuccessfully)
        {
            var bound = Task.Delay(DisposalBound, _timeProvider);
            if (await Task.WhenAny(stopping, bound).ConfigureAwait(false) != stopping)
                throw new TimeoutException("The device Stop did not settle during disposal.");
            try
            {
                await stopping.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }
        }

        try
        {
            await DisposeTransportAsync().ConfigureAwait(false);
        }
        catch (Exception transportFailure) when (stopFailure is not null)
        {
            throw new AggregateException(stopFailure, transportFailure);
        }

        if (stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
    }

    private async Task DisposeTransportAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
            if (ExecuteTask is { } executionTask)
            {
                try
                {
                    await executionTask.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (executionTask.IsCanceled && !timeout.IsCancellationRequested)
                {
                }
            }

            await _operationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                await DisposeCoreAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Device cleanup did not settle during disposal.");
        }

        lock (_stateGate)
        {
            _connectionState = DeviceConnectionState.Disconnected;
        }

        _operationGate.Dispose();
        _motionRevocation.Dispose();
        Dispose();
    }

    private void SetConnectionState(DeviceConnectionState state, DeviceInformation? device = null)
    {
        DeviceConnectionState previousState;
        CancellationTokenSource revoked;
        lock (_stateGate)
        {
            _device ??= device;
            if (state == DeviceConnectionState.Disconnected)
            {
                _lastRequestedSpeed = null;
                _pendingWrite = null;
                _pendingWrites.Clear();
            }

            if (_connectionState == state)
            {
                return;
            }

            previousState = _connectionState;
            _connectionState = state;
            _latestState = null;
            revoked = DisableCore();
        }

        CancelAndDispose(revoked);
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

}
