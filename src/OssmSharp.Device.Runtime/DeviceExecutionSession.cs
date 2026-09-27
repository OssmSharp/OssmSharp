using System.ComponentModel.DataAnnotations;
using OneOf;

namespace OssmSharp.Device.Runtime;

/// <summary>
/// Serializes one owner's device writes and latches Stop or externally observed changes
/// before waiting for an already-issued operation to settle.
/// </summary>
public sealed class DeviceExecutionSession : IAsyncDisposable
{
    private readonly IOssmDevice _device;
    private readonly TimeProvider _clock;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _ownerLifetime = new();
    private readonly ITimer _expiryTimer;
    private readonly Action<DeviceConnectionState> _connectionHandler;
    private readonly Action<OneOf<DeviceState, Exception>> _stateHandler;
    private DeviceState? _reported;
    private long _lastReportReceived;
    private Exception? _fault;
    private Task? _stopTask;
    private Task? _safetyStop;
    private Task? _preparationTask;
    private Task? _timedOutMotionTask;
    private PendingMotionFeedback? _pendingFeedback;
    private MotionSettings? _lastSubmitted;
    private Func<MotionSettings, bool>? _activeGuard;
    private CancellationTokenSource? _activeRevocation;
    private PendingMotionRequest? _pendingRequest;
    private long _generation;
    private long _interruptionRevision;
    private long _reportSequence;
    private long _lockReportSequence;
    private long _stoppedReportSequence;
    private bool _interrupted;
    private bool _initialized;
    private bool _disposed;
    private DeviceExecutionState _state;

    /// <summary>Attaches to the device before it starts so no initial report is lost.</summary>
    public DeviceExecutionSession(IOssmDevice device, TimeProvider clock)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _state = device.ConnectionState == DeviceConnectionState.Connected
            ? DeviceExecutionState.Paused : DeviceExecutionState.Disconnected;
        _connectionHandler = OnConnection;
        _stateHandler = OnState;
        device.ConnectionStateChanged += _connectionHandler;
        device.DeviceStateChanged += _stateHandler;
        _expiryTimer = clock.CreateTimer(
            static state => ((DeviceExecutionSession)state!).OnExpiry(),
            this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Gets the current execution condition. A connection alone is not ready.</summary>
    public DeviceExecutionState State
    {
        get { lock (_stateGate) return _state; }
    }

    /// <summary>Gets the latest real, validated reported state, if present.</summary>
    public DeviceState? Reported
    {
        get { lock (_stateGate) return _reported; }
    }

    /// <summary>Gets the current semantic fault, if any.</summary>
    public Exception? Fault
    {
        get { lock (_stateGate) return _fault; }
    }

    /// <summary>Gets whether an observed external change remains latched.</summary>
    public bool IsInterrupted
    {
        get { lock (_stateGate) return _interrupted; }
    }

    /// <summary>Gets the revision that must accompany an explicit acknowledgement.</summary>
    public long InterruptionRevision
    {
        get { lock (_stateGate) return _interruptionRevision; }
    }

    /// <summary>Gets the authority generation that must accompany a trusted request.</summary>
    public long AuthorityRevision
    {
        get { lock (_stateGate) return _generation; }
    }

    /// <summary>Gets whether initialization completed and no local inhibition is latched.</summary>
    public bool IsMotionReady
    {
        get
        {
            lock (_stateGate)
                return _initialized && !_interrupted && _fault is null
                    && _state == DeviceExecutionState.Ready
                    && HasFreshReport()
                    && _reported!.MotionSettings.State == MotionDeviceState.StrokeEngineIdle
                    && _stopTask is not { IsCompleted: false };
        }
    }

    /// <summary>
    /// Prepares a reported stopped play-state without navigation, then settles a zero
    /// request before accepting motion. Absent or stale feedback cannot establish readiness.
    /// </summary>
    /// <param name="expectedAuthorityRevision">The owner revision captured for this local initialization request.</param>
    /// <param name="cancellationToken">Cancels the caller's wait without releasing unsettled device I/O.</param>
    public async Task InitializeAsync(long expectedAuthorityRevision, CancellationToken cancellationToken)
    {
        Task preparation;
        lock (_stateGate)
        {
            CheckAlive();
            if (_generation != expectedAuthorityRevision)
                throw new InvalidOperationException("Device authority changed before initialization.");
            if (_preparationTask is { IsCompleted: false })
                throw new InvalidOperationException("Device initialization is already in progress.");
            preparation = InitializeCoreAsync(cancellationToken);
            _preparationTask = preparation;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        try
        {
            await preparation.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _generation++;
                _state = DeviceExecutionState.Faulted;
                _initialized = false;
                RevokeSynchronously(_activeRevocation);
                if (_reported is not null)
                    _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
            }
            // InitializeCoreAsync still owns the writer and its underlying native I/O.
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Device initialization did not settle before its deadline.");
        }
    }

    /// <summary>Submits a complete guarded tuple; a newer waiting tuple supersedes the previous one.</summary>
    /// <param name="settings">The complete desired control settings.</param>
    /// <param name="expectedAuthorityRevision">The owner revision captured with this request; Stop invalidates it.</param>
    /// <param name="guard">The synchronous trusted authority and permitted-envelope check, retained while output is active.</param>
    /// <param name="cancellationToken">Cancels this request without abandoning issued device I/O.</param>
    public async Task SetAsync(
        MotionSettings settings,
        long expectedAuthorityRevision,
        Func<MotionSettings, bool> guard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var operation = SetCoreAsync(settings, expectedAuthorityRevision, guard, cancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        try
        {
            await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _fault = new TimeoutException("Device write did not settle before its deadline.");
                _generation++;
                _state = DeviceExecutionState.Faulted;
                _initialized = false;
                RevokeSynchronously(_pendingRequest?.Revocation);
                RevokeSynchronously(_activeRevocation);
                _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
                _timedOutMotionTask = operation;
                _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
            }
            throw new TimeoutException(
                "Device write did not settle before its deadline; the issued operation remains owned.");
        }
    }

    /// <summary>
    /// Immediately invalidates queued motion, then waits behind the single in-flight writer
    /// and issues a zero-speed request even when feedback already reports zero.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            CheckAlive();
            if (_stopTask is { IsCompleted: false } existing)
                return AwaitStopAsync(existing, cancellationToken);
            _generation++;
            var pending = _pendingRequest;
            _pendingRequest = null;
            _activeGuard = null;
            RevokeSynchronously(pending?.Revocation);
            RevokeSynchronously(_activeRevocation);
            _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
            if (_state is not (DeviceExecutionState.Locked or DeviceExecutionState.Faulted))
                _state = DeviceExecutionState.Paused;
            _stopTask = StopCoreAsync();
            return AwaitStopAsync(_stopTask, cancellationToken);
        }
    }

    /// <summary>Acknowledges only the latest interruption after serialized zero and stopped evidence.</summary>
    /// <param name="expectedInterruptionRevision">The observed physical-interruption revision.</param>
    /// <param name="expectedAuthorityRevision">The owner revision captured before the acknowledgement request.</param>
    /// <param name="cancellationToken">Cancels waiting for the serialized Stop to settle.</param>
    public async Task<bool> AcknowledgeAsync(
        long expectedInterruptionRevision,
        long expectedAuthorityRevision,
        CancellationToken cancellationToken)
    {
        Task? stopping;
        lock (_stateGate)
        {
            CheckAlive();
            if (!_interrupted || _state != DeviceExecutionState.Locked || !_initialized)
                return false;
            if (expectedInterruptionRevision != _interruptionRevision
                || expectedAuthorityRevision != _generation)
                return false;
            stopping = _stopTask;
        }

        if (stopping is null)
            return false;
        await AwaitStopAsync(stopping, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (!_interrupted || _state != DeviceExecutionState.Locked || !_initialized
                || expectedInterruptionRevision != _interruptionRevision
                || expectedAuthorityRevision != _generation || !HasStoppedBaseline()
                || _stoppedReportSequence < _lockReportSequence)
                return false;
            _generation++;
            _interrupted = false;
            _fault = null;
            _state = DeviceExecutionState.Paused;
            return true;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            if (_device.ConnectionState == DeviceConnectionState.Connected
                && _reported is not null
                && _stopTask is not { IsCompleted: false }
                && (_state is DeviceExecutionState.Ready or DeviceExecutionState.Initializing
                    || _lastSubmitted?.Speed > 0))
            {
                _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
            }
            _disposed = true;
            _generation++;
            _state = DeviceExecutionState.Disposed;
            RevokeSynchronously(_pendingRequest?.Revocation);
            _pendingRequest = null;
            RevokeSynchronously(_activeRevocation);
            _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
        }
        _device.ConnectionStateChanged -= _connectionHandler;
        _device.DeviceStateChanged -= _stateHandler;
        _expiryTimer.Dispose();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        Exception? stopFailure = null;
        try
        {
            if (_stopTask is { } stopping)
            {
                try
                {
                    await stopping.WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (stopping.IsCompleted && !cleanup.IsCancellationRequested)
                {
                    // A Stop with no baseline before initialization issued no device I/O;
                    // there is no motion to settle, so disposal may still release resources.
                    if (_reported is not null || _initialized || _lastSubmitted is not null
                        || _preparationTask is not null || _timedOutMotionTask is not null
                        || _activeRevocation is not null)
                        stopFailure = exception;
                }
            }
            if (_preparationTask is { } preparing)
            {
                try
                {
                    await preparing.WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) when (preparing.IsCompleted && !cleanup.IsCancellationRequested)
                {
                    // Retain its reported failure, but finish releasing settled resources.
                }
            }
            if (_timedOutMotionTask is { } timedOut)
            {
                try
                {
                    await timedOut.WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) when (timedOut.IsCompleted && !cleanup.IsCancellationRequested)
                {
                    // Its unknown outcome was already reported; await settlement before release.
                }
            }
            if (_safetyStop is { } safety)
                await safety.WaitAsync(cleanup.Token).ConfigureAwait(false);
            await _writer.WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            // Retain the gate and its in-flight owner; cancellation cannot prove native I/O stopped.
            throw new TimeoutException("Device execution did not settle during cleanup.");
        }
        _writer.Release();
        _writer.Dispose();
        _ownerLifetime.Dispose();
        _activeRevocation?.Dispose();
        if (stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
    }

    private static IReadOnlyList<MotionSettings> BuildStoppedTransition(
        MotionSettings reported,
        MotionSettings desired)
    {
        if (reported.Stroke == desired.Stroke && reported.Depth == desired.Depth
            && reported.Sensation == desired.Sensation && reported.Buffer == desired.Buffer
            && reported.Pattern == desired.Pattern)
            return [];

        // Every envelope change begins with a settled, confirmed zero, even when
        // the current effective speed already reports zero.
        var zero = reported with { Speed = 0 };
        var steps = new List<MotionSettings> { zero };
        var smaller = zero with { Stroke = Math.Min(zero.Stroke, desired.Stroke) };
        if (smaller.Stroke != zero.Stroke)
            steps.Add(smaller);
        var depth = smaller with { Depth = desired.Depth };
        if (depth.Depth != smaller.Depth)
            steps.Add(depth);
        var stroke = depth with { Stroke = desired.Stroke };
        if (stroke.Stroke != depth.Stroke)
            steps.Add(stroke);
        var controls = stroke;
        if (controls.Sensation != desired.Sensation)
        {
            controls = controls with { Sensation = desired.Sensation };
            steps.Add(controls);
        }
        if (controls.Buffer != desired.Buffer)
        {
            controls = controls with { Buffer = desired.Buffer };
            steps.Add(controls);
        }
        if (controls.Pattern != desired.Pattern)
        {
            controls = controls with { Pattern = desired.Pattern };
            steps.Add(controls);
        }
        return steps;
    }

    private static bool ReportedMatches(MotionSettings requested, MotionSettings? reported) =>
        reported is { State: MotionDeviceState.StrokeEngineIdle }
        && reported.Stroke == requested.Stroke
        && reported.Sensation == requested.Sensation
        && reported.Depth == requested.Depth
        && reported.Buffer == requested.Buffer
        && reported.Pattern == requested.Pattern
        && (requested.Speed == 0
            ? reported.Speed == 0
            : reported.Speed > 0 && reported.Speed <= requested.Speed);

    // Stop must revoke before returning and before the next native write can
    // pass its token check; deferring cancellation would break that ordering.
    private static void RevokeSynchronously(CancellationTokenSource? revocation) =>
        revocation?.Cancel();

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        MotionSettings zero;
        long generation;
        lock (_stateGate)
        {
            CheckAlive();
            if (_state is not DeviceExecutionState.Paused || _interrupted || !HasStoppedBaseline())
                throw new InvalidOperationException("Recent stopped device feedback is required for initialization.");
            _state = DeviceExecutionState.Initializing;
            zero = _reported!.MotionSettings with { Speed = 0 };
            generation = _generation;
        }

        CancellationTokenSource? preparation = null;
        var writerOwned = false;
        try
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            writerOwned = true;
            lock (_stateGate)
            {
                if (_generation != generation || _state != DeviceExecutionState.Initializing
                    || !HasStoppedBaseline())
                    throw new OperationCanceledException("Initialization was interrupted.");
                preparation = CancellationTokenSource.CreateLinkedTokenSource(
                    _ownerLifetime.Token, cancellationToken);
                _activeRevocation = preparation;
            }
            Task issued;
            lock (_stateGate)
            {
                if (_generation != generation || _state != DeviceExecutionState.Initializing
                    || !HasStoppedBaseline())
                    throw new OperationCanceledException("Initialization was interrupted.");
                preparation.Token.ThrowIfCancellationRequested();
                issued = _device.SetMotionAsync(zero, preparation.Token);
            }
            await issued.ConfigureAwait(false);
            if (_device is IDevicePreparation preflight)
            {
                Task configuration;
                lock (_stateGate)
                {
                    if (_generation != generation || _state != DeviceExecutionState.Initializing
                        || !HasStoppedBaseline())
                        throw new OperationCanceledException("Initialization was interrupted.");
                    preparation.Token.ThrowIfCancellationRequested();
                    configuration = preflight.PrepareAsync(preparation.Token, _ownerLifetime.Token);
                }
                await configuration.ConfigureAwait(false);
            }
            lock (_stateGate)
            {
                if (_generation != generation || _state != DeviceExecutionState.Initializing
                    || !HasStoppedBaseline())
                    throw new OperationCanceledException("Initialization was interrupted.");
                _lastSubmitted = zero;
                _initialized = true;
                _state = DeviceExecutionState.Ready;
            }
        }
        catch
        {
            lock (_stateGate)
            {
                if (_generation == generation && _state == DeviceExecutionState.Initializing)
                {
                    _generation++;
                    _state = DeviceExecutionState.Faulted;
                    _initialized = false;
                }
            }
            throw;
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_activeRevocation, preparation))
                    _activeRevocation = null;
            }
            preparation?.Dispose();
            if (writerOwned)
                _writer.Release();
        }
    }

    private async Task SetCoreAsync(
        MotionSettings settings,
        long expectedAuthorityRevision,
        Func<MotionSettings, bool> guard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        long generation;
        var issuedTransition = false;
        var rejectedBeforeIssuance = false;
        using var pendingRevocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var request = new PendingMotionRequest(
            settings, expectedAuthorityRevision, guard, pendingRevocation);
        lock (_stateGate)
        {
            CheckAlive();
            if (_generation != request.AuthorityRevision)
                throw new InvalidOperationException("Device authority changed before motion submission.");
            if (!CanWrite(settings, guard))
                throw new InvalidOperationException("Device is not ready for motion.");
            var reported = _reported!.MotionSettings;
            var steps = BuildStoppedTransition(reported, settings);
            ValidateProviderTransition(reported, steps, settings);
            if (!AreTransitionStepsPermitted(steps, guard))
                throw new InvalidOperationException("A stopped transition step is outside the permitted envelope.");
            RevokeSynchronously(_pendingRequest?.Revocation);
            _pendingRequest = request;
            generation = _generation;
        }
        try
        {
            await _writer.WaitAsync(request.Revocation.Token).ConfigureAwait(false);
            CancellationTokenSource? operationRevocation = null;
            try
            {
                MotionSettings reported;
                IReadOnlyList<MotionSettings> transition;
                lock (_stateGate)
                {
                    if (_generation != request.AuthorityRevision
                        || !ReferenceEquals(_pendingRequest, request)
                        || !CanWrite(request.Settings, request.Guard))
                        throw new OperationCanceledException("Motion was superseded by Stop or lost readiness.");
                    request.Revocation.Token.ThrowIfCancellationRequested();
                    reported = _reported!.MotionSettings;
                    transition = BuildStoppedTransition(reported, request.Settings);
                    try
                    {
                        ValidateProviderTransition(reported, transition, request.Settings);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
                    {
                        rejectedBeforeIssuance = true;
                        throw;
                    }
                    if (!AreTransitionStepsPermitted(transition, request.Guard))
                    {
                        rejectedBeforeIssuance = true;
                        throw new InvalidOperationException(
                            "A stopped transition step is outside the permitted envelope.");
                    }
                    _pendingRequest = null;
                    issuedTransition = true;
                    // Issue while holding the synchronous state owner: Stop cannot latch
                    // between the last generation check and issuance.
                    operationRevocation = CancellationTokenSource.CreateLinkedTokenSource(
                        _ownerLifetime.Token, cancellationToken);
                    _activeRevocation = operationRevocation;
                }

                foreach (var intermediate in transition)
                {
                    await ApplyStepAsync(intermediate, guard, generation, operationRevocation.Token)
                        .ConfigureAwait(false);
                }
                await ApplyStepAsync(settings, guard, generation, operationRevocation.Token)
                    .ConfigureAwait(false);
                lock (_stateGate)
                {
                    if (_generation != generation)
                        throw new OperationCanceledException("Motion was superseded by Stop.");
                    if (_fault is not null)
                        throw new InvalidDataException("Device reported an unexpected motion change.", _fault);
                    _lastSubmitted = settings;
                    _activeGuard = settings.Speed > 0 ? guard : null;
                }
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_activeRevocation, operationRevocation))
                        _activeRevocation = null;
                }
                operationRevocation?.Dispose();
                _writer.Release();
            }
        }
        catch (OperationCanceledException)
        {
            bool shouldStop;
            lock (_stateGate)
            {
                if (ReferenceEquals(_pendingRequest, request))
                    _pendingRequest = null;
                shouldStop = issuedTransition && _generation == generation && !_disposed;
            }
            if (shouldStop)
            {
                using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await StopAsync(stopBudget.Token).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception) when (rejectedBeforeIssuance && !issuedTransition)
        {
            throw;
        }
        catch (Exception exception)
        {
            Task? alreadyStopping;
            lock (_stateGate)
            {
                alreadyStopping = _state == DeviceExecutionState.Faulted
                    && ReferenceEquals(_fault, exception) ? _stopTask : null;
                if (alreadyStopping is null)
                {
                    _fault = exception;
                    _generation++;
                    _state = DeviceExecutionState.Faulted;
                    _initialized = false;
                }
            }
            using (var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    if (alreadyStopping is not null)
                        await alreadyStopping.WaitAsync(stopBudget.Token).ConfigureAwait(false);
                    else
                        await StopAsync(stopBudget.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Keep the original fault and the outstanding Stop owner visible.
                }
            }
            throw;
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_pendingRequest, request))
                    _pendingRequest = null;
            }
        }
    }

    private async Task ApplyStepAsync(
        MotionSettings settings,
        Func<MotionSettings, bool> guard,
        long generation,
        CancellationToken revocation)
    {
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        PendingMotionFeedback feedback;
        Task issued;
        lock (_stateGate)
        {
            if (_generation != generation || !CanWrite(settings, guard, forIssuance: true))
                throw new OperationCanceledException("Motion transition lost readiness before its next write.");
            if (_device is IDeviceTransitionValidator validator)
                validator.ValidateTransition(_reported!.MotionSettings, [settings]);
            revocation.ThrowIfCancellationRequested();
            feedback = new PendingMotionFeedback(
                settings, generation, _reportSequence,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingFeedback = feedback;
            // An unchanged zero report is an existing valid baseline, not a new sample.
            if (settings.Speed == 0 && ReportedMatches(settings, _reported?.MotionSettings))
                feedback.Completion.TrySetResult();
            try
            {
                issued = _device.SetMotionAsync(settings, revocation);
            }
            catch
            {
                _pendingFeedback = null;
                throw;
            }
        }

        try
        {
            // Retain ownership of the actual issued operation even when Stop revoked
            // its pre-write token. Only subsequent writes are blocked.
            await issued.ConfigureAwait(false);
            using var feedbackDeadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(2), _clock);
            try
            {
                await feedback.Completion.Task.WaitAsync(feedbackDeadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (feedbackDeadline.IsCancellationRequested)
            {
                throw new TimeoutException("Device motion feedback was not confirmed before its deadline.");
            }
            lock (_stateGate)
            {
                if (_generation != generation || _fault is not null || !EvaluateGuard(settings, guard))
                    throw new OperationCanceledException("Motion transition was interrupted.");
                revocation.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_pendingFeedback, feedback))
                    _pendingFeedback = null;
            }
        }
    }

    private async Task StopCoreAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            MotionSettings zero;
            lock (_stateGate)
            {
                if (_reported is not { } report)
                    throw new InvalidOperationException(
                        "Cannot derive a safe Stop tuple without reported device settings.");
                zero = report.MotionSettings with { Speed = 0 };
            }
            if (_device is IDeviceSafetyStop safetyStop)
                await safetyStop.SetSafetyStopAsync(zero, _ownerLifetime.Token).ConfigureAwait(false);
            else
                await _device.SetMotionAsync(zero, _ownerLifetime.Token).ConfigureAwait(false);
            lock (_stateGate) _lastSubmitted = zero;
        }
        catch
        {
            lock (_stateGate)
            {
                _state = DeviceExecutionState.Faulted;
                _initialized = false;
                _generation++;
            }
            throw;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task AwaitStopAsync(Task stop, CancellationToken cancellationToken)
    {
        try
        {
            await stop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _generation++;
                _state = DeviceExecutionState.Faulted;
                _initialized = false;
            }
            throw;
        }
    }

    private void OnConnection(DeviceConnectionState connection)
    {
        lock (_stateGate)
        {
            _generation++;
            _reported = null;
            _initialized = false;
            _activeGuard = null;
            RevokeSynchronously(_pendingRequest?.Revocation);
            _pendingRequest = null;
            RevokeSynchronously(_activeRevocation);
            _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
            if (connection == DeviceConnectionState.Connected && !_interrupted)
                _state = DeviceExecutionState.Paused;
            else if (!_interrupted)
                _state = DeviceExecutionState.Disconnected;
        }
    }

    private void OnState(OneOf<DeviceState, Exception> change)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            if (change.IsT0)
            {
                _reported = change.AsT0;
                _reportSequence++;
                _lastReportReceived = _clock.GetTimestamp();
                if (_reported.MotionSettings is { State: MotionDeviceState.StrokeEngineIdle, Speed: 0 })
                    _stoppedReportSequence = _reportSequence;
                if (_initialized && _reported.MotionSettings.State != MotionDeviceState.StrokeEngineIdle)
                {
                    _fault = new InvalidDataException("The device left its verified play-ready state.");
                    _generation++;
                    _state = DeviceExecutionState.Faulted;
                    _initialized = false;
                    RevokeSynchronously(_pendingRequest?.Revocation);
                    _pendingRequest = null;
                    RevokeSynchronously(_activeRevocation);
                    _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
                    _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
                    return;
                }
                if (_pendingFeedback is { } feedback
                    && feedback.Generation == _generation
                    && _reportSequence > feedback.IssuedAfterReportSequence
                    && ReportedMatches(feedback.Settings, _reported.MotionSettings))
                    feedback.Completion.TrySetResult();
                return;
            }
            var alreadyFaulted = _state == DeviceExecutionState.Faulted;
            _fault = alreadyFaulted ? _fault ?? change.AsT1 : change.AsT1;
            _generation++;
            _activeGuard = null;
            RevokeSynchronously(_pendingRequest?.Revocation);
            _pendingRequest = null;
            RevokeSynchronously(_activeRevocation);
            _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
            if (_fault is UnexpectedMotionChangeException)
            {
                _interrupted = true;
                _interruptionRevision++;
                _lockReportSequence = _reportSequence;
                _state = alreadyFaulted ? DeviceExecutionState.Faulted : DeviceExecutionState.Locked;
                if (alreadyFaulted)
                    _initialized = false;
                _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
            }
            else
            {
                _state = DeviceExecutionState.Faulted;
                _initialized = false;
                _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
            }
        }
    }

    private void OnExpiry()
    {
        lock (_stateGate)
        {
            if (_disposed || !_initialized || _state != DeviceExecutionState.Ready
                || _lastSubmitted?.Speed is not > 0)
                return;

            bool authorized;
            try
            {
                authorized = _activeGuard is { } guard
                    && EvaluateGuard(_lastSubmitted, guard);
            }
            catch
            {
                // EvaluateGuard has already latched the fault and serialized Stop.
                return;
            }

            if (authorized && HasFreshReport())
                return;
            _fault = authorized
                ? new TimeoutException("Device feedback expired; readiness and physical state are unconfirmed.")
                : new InvalidOperationException("The in-process motion guard revoked active authority.");
            _generation++;
            _state = DeviceExecutionState.Faulted;
            _initialized = false;
            RevokeSynchronously(_activeRevocation);
            _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
            _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
        }
    }

    private async Task ObserveSafetyStopAsync(Task stop)
    {
        try
        {
            await stop.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                _fault ??= exception;
                _state = DeviceExecutionState.Faulted;
                _initialized = false;
            }
        }
    }

    private bool HasFreshReport() =>
        _device.ConnectionState == DeviceConnectionState.Connected
        && _reported is { } report
        && report.Timestamp <= _clock.GetUtcNow().UtcDateTime
        && _clock.GetUtcNow().UtcDateTime - report.Timestamp <= TimeSpan.FromSeconds(5)
        && _clock.GetElapsedTime(_lastReportReceived) <= TimeSpan.FromSeconds(5);

    private bool HasStoppedBaseline() =>
        HasFreshReport()
        && _reported!.MotionSettings is { State: MotionDeviceState.StrokeEngineIdle, Speed: 0 };

    private bool CanWrite(
        MotionSettings settings,
        Func<MotionSettings, bool> guard,
        bool forIssuance = false)
    {
        if (!_initialized || _interrupted || _fault is not null
            || _state is not (DeviceExecutionState.Ready or DeviceExecutionState.Paused)
            || (settings.Speed != 0 && _state != DeviceExecutionState.Ready)
            || !HasFreshReport() || _stopTask is { IsCompleted: false }
            || (_state == DeviceExecutionState.Paused && !HasStoppedBaseline())
            || !_device.Device.SupportedPatterns.Contains(settings.Pattern)
            || !EvaluateGuard(settings, guard))
            return false;

        var reported = _reported!.MotionSettings;
        if (reported.State != MotionDeviceState.StrokeEngineIdle)
            return false;
        return !forIssuance || reported.Speed == 0
            || (reported.Stroke == settings.Stroke && reported.Depth == settings.Depth
                && reported.Pattern == settings.Pattern);
    }

    private bool EvaluateGuard(MotionSettings settings, Func<MotionSettings, bool> guard)
    {
        try
        {
            return guard(settings);
        }
        catch (Exception exception)
        {
            _fault = exception;
            _generation++;
            _initialized = false;
            _activeGuard = null;
            _state = DeviceExecutionState.Faulted;
            RevokeSynchronously(_pendingRequest?.Revocation);
            _pendingRequest = null;
            RevokeSynchronously(_activeRevocation);
            _pendingFeedback?.Completion.TrySetCanceled(CancellationToken.None);
            if (_reported is not null)
                _safetyStop = ObserveSafetyStopAsync(StopAsync(_ownerLifetime.Token));
            throw;
        }
    }

    private void ValidateProviderTransition(
        MotionSettings reported,
        IReadOnlyList<MotionSettings> transition,
        MotionSettings desired)
    {
        if (_device is IDeviceTransitionValidator validator)
            validator.ValidateTransition(reported, [.. transition, desired]);
    }

    private bool AreTransitionStepsPermitted(
        IReadOnlyList<MotionSettings> transition,
        Func<MotionSettings, bool> guard)
    {
        foreach (var step in transition)
        {
            if (!EvaluateGuard(step, guard))
                return false;
        }
        return true;
    }

    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}
