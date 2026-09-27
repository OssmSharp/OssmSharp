using OneOf;
using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Application;

/// <summary>Coordinates device execution, safety policy, and the single device writer.</summary>
public sealed class ControlEngine : IAsyncDisposable
{
    private const string SimulatorProfile = "ossm-rs-sim-v1";
    private static readonly TimeSpan FeedbackFreshness = TimeSpan.FromMilliseconds(1500);
    private static readonly MotionSettings Stopped = new(
        MotionDeviceState.StrokeEngineIdle,
        0,
        10,
        50,
        20,
        0,
        MotionPattern.SimpleStroke,
        0);
    private readonly object _gate = new();
    private readonly IOssmDevice _device;
    private readonly TimeProvider _time;
    private DeviceState? _observed;
    private long _sampleTimestamp;
    private long _observationSequence;
    private long _connection;
    private bool _connected;
    private bool _hasSample;
    private string _mode = "Initializing";
    private string _reason = "awaiting-feedback";
    private bool _locked;
    private long _lockRevision;
    private long _generation;
    private ControlLimits _limits = new(100, 100);
    private long? _authorizationDeadline;
    private MotionSettings _desired = Stopped;
    private bool _hasDesired;
    private bool _envelopeStopped;
    private MotionSettings? _zeroAwaitingEcho;
    private bool _stopRequested;
    private bool _stopQueued;
    private StopFeedback? _stopFeedback;
    private Operation? _operation;
    private MotionSettings? _lastRequestedSettings;
    private int _unobservedCommandSpeed;
    private int _unobservedCommandDepth;
    private Task? _disposeTask;

    /// <summary>
    /// Creates the admin-owned controller with its device boundary and monotonic clock.
    /// </summary>
    /// <param name="device">The single owned device writer and feedback source.</param>
    /// <param name="timeProvider">The clock used for deadlines and feedback freshness.</param>
    public ControlEngine(IOssmDevice device, TimeProvider timeProvider)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _device.ConnectionStateChanged += OnConnectionStateChanged;
        _device.DeviceStateChanged += OnDeviceStateChanged;
    }

    /// <summary>Gets the current controller state for trusted admin use.</summary>
    public ControlExecutionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return MakeSnapshot(_time.GetTimestamp());
        }
    }

    /// <summary>
    /// Applies compatible limits atomically without rearming control.
    /// </summary>
    /// <param name="limits">The proposed speed and depth caps, each from 0 to 100.</param>
    /// <exception cref="ControlRejectedException">The limits are invalid or conflict with pending or observed motion.</exception>
    public void SetLimits(ControlLimits limits)
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(limits is not null && limits.SpeedCap is >= 0 and <= 100 &&
                limits.MaximumDepth is >= 0 and <= 100, "invalid-limits");
            if (limits == _limits)
                return;

            // Validate before any mutation: even revoked I/O owns the writer until it
            // settles, and an accepted replacement does not erase the current settings.
            Require(FitsProposedLimits(limits!), "limits-conflict");
            var now = _time.GetTimestamp();
            if (_mode == "Ready" && AuthorizationExpired(now))
                PauseCore("grant-expired");

            _limits = limits!;
        }
    }

    private bool FitsProposedLimits(ControlLimits limits)
    {
        if (_unobservedCommandSpeed > limits.SpeedCap || _unobservedCommandDepth > limits.MaximumDepth)
            return false;
        if ((_authorizationDeadline is not null || _hasDesired) && !ValidDesired(_desired, limits))
            return false;
        if (_observed is { } observed &&
            IsMoving(observed) &&
            !ValidDesired(observed.MotionSettings, limits))
            return false;
        if (_operation is { Kind: not OperationKind.Stop } operation && !ValidDesired(operation.Desired, limits))
            return false;
        return _zeroAwaitingEcho is null || ValidDesired(_zeroAwaitingEcho, limits);
    }

    /// <summary>Arms execution when trusted authorization, policy, and device safety checks succeed.</summary>
    public void Arm()
    {
        lock (_gate)
        {
            CheckAvailable();
            var now = _time.GetTimestamp();
            ArmCore(now);
        }
    }

    /// <summary>
    /// Pauses control and invalidates active guest motion authority.
    /// </summary>
    /// <param name="reason">The reason recorded in the controller snapshot.</param>
    public void Pause(string reason = "operator-stop")
    {
        lock (_gate)
        {
            CheckAvailable();
            PauseCore(reason);
        }
    }

    /// <summary>
    /// Clears a matching external lock and returns the controller to Paused.
    /// </summary>
    /// <param name="expectedRevision">The currently observed external-lock revision.</param>
    /// <exception cref="ControlRejectedException">The lock revision differs or a safe stopped state is not established.</exception>
    public void Unlock(long expectedRevision)
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(_locked && expectedRevision == _lockRevision, "lock-revision-mismatch");
            Require(NoOutstandingIo(), "operation-pending");
            Require(KnownStopped(_time.GetTimestamp()), "device-not-stopped");
            _locked = false;
            InvalidateAuthority();
            _mode = "Paused";
            _reason = "operator-unlocked";
        }
    }

    /// <summary>Returns a recoverable controller to Paused after device safety checks.</summary>
    public void Recover()
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(!_locked, "external-lock");
            Require(_mode is "Initializing" or "Faulted" or "Paused", "not-recoverable");
            Require(NoOutstandingIo(), "operation-pending");
            Require(KnownStopped(_time.GetTimestamp()), "device-not-stopped");
            InvalidateAuthority();
            _mode = "Paused";
            _reason = "recovered";
            _stopRequested = false;
        }
    }

    /// <summary>
    /// Accepts trusted desired settings until the supplied monotonic authorization deadline.
    /// </summary>
    /// <param name="desired">The desired motion settings, subject to active policy and fresh feedback.</param>
    /// <param name="authorizationDeadline">The monotonic clock timestamp at which authority expires.</param>
    /// <exception cref="ControlRejectedException">The controller is not armed, feedback is stale, or the settings or deadline are invalid.</exception>
    public void Submit(MotionSettings desired, long authorizationDeadline)
    {
        lock (_gate)
        {
            CheckAvailable();
            var now = _time.GetTimestamp();
            if (AuthorizationExpired(now))
                PauseCore("grant-expired");
            Require(_mode == "Ready" && !_locked, "not-armed");
            Require(_time.GetElapsedTime(now, authorizationDeadline) > TimeSpan.Zero, "grant-invalid");
            Require(ValidDesired(desired), "desired-invalid");
            Require(Fresh(now), "feedback-stale");
            _authorizationDeadline = authorizationDeadline;
            _desired = desired;
            _hasDesired = true;
        }
    }

    /// <summary>Processes deadlines, feedback freshness, and queued device work.</summary>
    public void Tick()
    {
        Operation? operation;
        lock (_gate)
        {
            if (_disposeTask is not null)
                return;
            var now = _time.GetTimestamp();
            ObserveOperation(now);
            if (_hasSample && _connected && !Fresh(now))
                Fault("feedback-stale");
            ObserveStopFeedback(now);
            if (_mode == "Ready" && AuthorizationExpired(now))
                PauseCore("grant-expired");

            operation = DispatchNext(now);
        }
        if (operation is not null)
            StartOperation(operation);
    }

    private void PauseCore(string reason)
    {
        if (!_stopRequested || _mode == "Ready")
        {
            InvalidateAuthority();
            RequestStop();
        }
        if (_mode is not "Locked" and not "Faulted")
        {
            _mode = "Paused";
            _reason = reason;
        }
    }

    private void InvalidateAuthority()
    {
        _generation++;
        _authorizationDeadline = null;
        _hasDesired = false;
        _envelopeStopped = false;
        _zeroAwaitingEcho = null;
        _desired = (_observed?.MotionSettings ?? Stopped) with { Speed = 0 };
    }

    private void RequestStop(bool force = false)
    {
        _stopRequested = true;
        if (force || (_operation?.Kind != OperationKind.Stop && _stopFeedback is null))
            _stopQueued = true;
    }

    private void Fault(string reason)
    {
        if ((_mode == "Faulted" || _locked) && _reason == reason)
            return;
        InvalidateAuthority();
        _mode = _locked ? "Locked" : "Faulted";
        _reason = reason;
        RequestStop();
    }

    private void ExternalChange()
    {
        _locked = true;
        _lockRevision++;
        InvalidateAuthority();
        _mode = "Locked";
        _reason = "external-parameter-change";
        RequestStop(force: true);
    }

    private void ArmCore(long now)
    {
        Require(!_locked, "external-lock");
        Require(NoOutstandingIo(), "operation-pending");
        Require(_mode == "Paused", "not-paused");
        Require(PolicyAllowsArming(), "limits-not-configured");
        Require(KnownStopped(now), "device-not-stopped");
        _generation++;
        _mode = "Ready";
        _reason = "armed";
        _authorizationDeadline = null;
        _hasDesired = false;
        _envelopeStopped = false;
        _zeroAwaitingEcho = null;
        _desired = _observed!.MotionSettings with { Speed = 0 };
        _stopRequested = false;
    }

    private void OnConnectionStateChanged(DeviceConnectionState state)
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return;

            if (state != DeviceConnectionState.Connected)
            {
                _connected = false;
                ClearObservationBaseline();
                if (state == DeviceConnectionState.Disconnected)
                    Fault("bridge-disconnected");
                return;
            }

            _connection++;
            _connected = true;
            ClearObservationBaseline();
        }
    }

    private void OnDeviceStateChanged(OneOf<DeviceState, Exception> change) =>
        change.Switch(OnState, OnDeviceError);

    private void OnState(DeviceState state)
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return;
            if (!_connected)
            {
                Fault("invalid-feedback");
                return;
            }

            var now = _time.GetTimestamp();
            _observationSequence++;
            _hasSample = true;
            _sampleTimestamp = now;
            _observed = state;
            if (_lastRequestedSettings is { } requested && MatchesRequestedControl(requested, state.MotionSettings))
            {
                _unobservedCommandSpeed = 0;
                _unobservedCommandDepth = 0;
            }
        }
    }

    private void OnDeviceError(Exception error)
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return;
            if (error is UnexpectedMotionChangeException)
                ExternalChange();
            else
                Fault("device-error");
        }
    }

    private bool IsMatchingFeedback(DeviceState state, MotionSettings expected) =>
        _lastRequestedSettings is { } requested &&
        SameRequestedControl(requested, expected) &&
        MatchesRequestedControl(expected, state.MotionSettings);

    private void ClearObservationBaseline()
    {
        _hasSample = false;
        _observed = null;
        _sampleTimestamp = 0;
        _observationSequence = 0;
        _lastRequestedSettings = null;
        _unobservedCommandSpeed = 0;
        _unobservedCommandDepth = 0;
        _zeroAwaitingEcho = null;
    }

    private void ObserveOperation(long now)
    {
        if (_operation is not { } operation)
            return;
        if (!operation.Task.IsCompleted)
        {
            if (!operation.TimedOut && Elapsed(operation.StartedAt, now) >= TimeSpan.FromSeconds(1))
            {
                operation.TimedOut = true;
                Fault("bridge-operation-timeout");
                RequestStop(force: true);
            }
            return;
        }

        var completedAt = operation.CompletedAt ?? now;
        if (!operation.TimedOut && Elapsed(operation.StartedAt, completedAt) >= TimeSpan.FromSeconds(1))
        {
            operation.TimedOut = true;
            Fault("bridge-operation-timeout");
            RequestStop(force: true);
        }
        _operation = null;
        try
        {
            operation.Task.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            Fault("bridge-operation-failed");
            return;
        }

        if (operation.Kind == OperationKind.Stop)
        {
            _stopFeedback = new(completedAt, operation.SampleSequence, operation.Connection,
                operation.Generation, operation.ForEnvelope && !operation.TimedOut);
        }
        else if (!operation.TimedOut && operation.Generation == _generation &&
            operation.Kind == OperationKind.Motion && _desired == operation.Desired)
        {
            _hasDesired = false;
        }
    }

    private void ObserveStopFeedback(long now)
    {
        if (_stopFeedback is not { } feedback)
            return;
        if (KnownStopped(now) && _connection == feedback.Connection &&
            _observationSequence > feedback.SampleSequence)
        {
            _stopFeedback = null;
            if (feedback.ForEnvelope && feedback.Generation == _generation && _mode == "Ready")
                _envelopeStopped = true;
        }
        else if (Elapsed(feedback.CompletedAt, now) >= FeedbackFreshness)
        {
            _stopFeedback = null;
            Fault("stop-feedback-timeout");
        }
    }

    private Operation? DispatchNext(long now)
    {
        if (_operation is not null)
            return null;
        if (_stopQueued)
        {
            _stopQueued = false;
            _stopFeedback = null;
            return ReserveOperation(
                OperationKind.Stop,
                (_observed?.MotionSettings ?? Stopped) with { Speed = 0 },
                now);
        }
        if (_stopFeedback is not null || _mode != "Ready" || !_hasDesired || !MotionAuthorized(now))
            return null;

        if (_envelopeStopped)
        {
            if (_zeroAwaitingEcho is null)
            {
                _zeroAwaitingEcho = _desired with { Speed = 0 };
                return ReserveOperation(OperationKind.Zero, _zeroAwaitingEcho, now);
            }
            if (!IsMatchingFeedback(_observed!, _zeroAwaitingEcho) || !KnownStopped(now))
                return null;
            if (!SameEnvelope(_desired, _zeroAwaitingEcho))
            {
                _zeroAwaitingEcho = null;
                return null;
            }
            _envelopeStopped = false;
            _zeroAwaitingEcho = null;
        }

        if (!SameEnvelope(_observed!.MotionSettings, _desired))
            return ReserveOperation(
                OperationKind.Stop,
                _observed.MotionSettings with { Speed = 0 },
                now,
                forEnvelope: true);
        if (!IsMatchingFeedback(_observed, _desired))
            return ReserveOperation(OperationKind.Motion, _desired, now);

        _hasDesired = false;
        return null;
    }

    private bool MotionAuthorized(long now)
    {
        if (_mode != "Ready" || _locked || _authorizationDeadline is null)
            return false;
        if (AuthorizationExpired(now))
        {
            PauseCore("grant-expired");
            return false;
        }
        if (!Fresh(now))
        {
            Fault("feedback-stale");
            return false;
        }
        return ValidDesired(_desired);
    }

    private Operation? ReserveOperation(OperationKind kind, MotionSettings desired, long now, bool forEnvelope = false)
    {
        if (kind != OperationKind.Stop && !MotionAuthorized(now))
            return null;
        var operation = new Operation(kind, desired, now, _generation,
            _observationSequence, _connection, forEnvelope);
        _operation = operation;
        _lastRequestedSettings = desired;
        if (kind != OperationKind.Stop)
        {
            // Completion need not include feedback. Retain bounded maxima until an
            // owned echo establishes the current tuple, even across replacement I/O.
            _unobservedCommandSpeed = Math.Max(_unobservedCommandSpeed, desired.Speed);
            _unobservedCommandDepth = Math.Max(_unobservedCommandDepth, desired.Depth);
        }
        return operation;
    }

    private void StartOperation(Operation operation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_operation, operation) || operation.Claimed)
                return;
            operation.Claimed = true;
            if (operation.Kind != OperationKind.Stop &&
                (_disposeTask is not null || operation.Generation != _generation ||
                 _stopQueued || !MotionAuthorized(_time.GetTimestamp())))
            {
                operation.Start(Task.CompletedTask);
                return;
            }
        }

        Task task;
        try
        {
            task = _device.SetMotionAsync(operation.Desired, CancellationToken.None);
        }
        catch (Exception error)
        {
            task = Task.FromException(error);
        }
        lock (_gate)
            operation.Start(task);
        _ = task.ContinueWith(completed =>
        {
            var completedAt = _time.GetTimestamp();
            _ = completed.Exception;
            lock (_gate)
                operation.CompletedAt ??= completedAt;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private ControlExecutionSnapshot MakeSnapshot(long now)
    {
        return new(_mode, _reason, _generation, _lockRevision, Fresh(now), IsMoving(_observed),
            _desired, _observed, _limits)
        {
            OperationPending = !NoOutstandingIo(),
            DeviceEnabled = _observed?.MotionSettings.Speed > 0,
            IsSimulator = IsSimulator(),
            Position = Fresh(now) ? _observed?.MotionSettings.Position : null,
            Velocity = Fresh(now) ? _observed?.VelocityMillimetresPerSecond : null,
            Current = Fresh(now) ? _observed?.MotionSettings : null,
            DesiredConfirmed = Fresh(now) && !_hasDesired && NoOutstandingIo() &&
                MatchesRequestedControl(_desired, _observed!.MotionSettings) &&
                IsMatchingFeedback(_observed, _desired)
        };
    }

    private bool Fresh(long now) => _hasSample && _connected &&
        Elapsed(_sampleTimestamp, now) < FeedbackFreshness;

    private bool KnownStopped(long now) => Fresh(now) &&
        _observed?.MotionSettings.Speed == 0 && !IsMoving(_observed);

    private bool IsSimulator() => _connected && _device.Device.ProfileId == SimulatorProfile;

    private static bool IsMoving(DeviceState? state) => state is not null &&
        (state.MotionSettings.Speed > 0 || state.VelocityMillimetresPerSecond is not null and not 0);

    private bool NoOutstandingIo() => _operation is null && !_stopQueued && _stopFeedback is null;

    private bool PolicyAllowsArming() => _limits.SpeedCap is > 0 and <= 100 && _limits.MaximumDepth is > 0 and <= 100;

    private bool ValidDesired(MotionSettings? desired) => ValidDesired(desired, _limits);

    private static bool ValidDesired(MotionSettings? desired, ControlLimits limits) =>
        desired is { Pattern: 0, Sensation: 50, Speed: >= 0 and <= 100, Stroke: >= 0 and <= 100, Depth: >= 0 and <= 100 } &&
        desired.Stroke <= desired.Depth && desired.Depth <= limits.MaximumDepth &&
        desired.Speed <= limits.SpeedCap && (desired.Speed == 0 || desired.Stroke > 0);

    private static bool SameEnvelope(MotionSettings first, MotionSettings second) =>
        first.Pattern == second.Pattern && first.Stroke == second.Stroke &&
        first.Depth == second.Depth && first.Sensation == second.Sensation;

    private static bool MatchesRequestedControl(MotionSettings requested, MotionSettings reported) =>
        SameEnvelope(requested, reported) &&
        (requested.Speed == 0 ? reported.Speed == 0 : reported.Speed >= 0 && reported.Speed <= requested.Speed);

    private static bool SameRequestedControl(MotionSettings first, MotionSettings second) =>
        first.Speed == second.Speed && SameEnvelope(first, second);

    private TimeSpan Elapsed(long start, long end) => _time.GetElapsedTime(start, end);
    private bool AuthorizationExpired(long now) =>
        _authorizationDeadline is { } deadline && _time.GetElapsedTime(now, deadline) <= TimeSpan.Zero;

    private static ControlRejectedException Rejection(string code) => new(code);
    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw Rejection(code);
    }
    private void CheckAvailable() => Require(_disposeTask is null, "engine-disposed");

    /// <summary>Stops the controller and releases its device bridge resources.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new(_disposeTask);
            PauseCore("disposed");
            _device.ConnectionStateChanged -= OnConnectionStateChanged;
            _device.DeviceStateChanged -= OnDeviceStateChanged;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = DrainDisposalAsync(completion);
        return new(completion.Task);
    }

    private async Task DrainDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                Task? task;
                Operation? operation;
                lock (_gate)
                {
                    ObserveOperation(_time.GetTimestamp());
                    operation = _operation is null && _stopQueued
                        ? DispatchNext(_time.GetTimestamp())
                        : null;
                    task = _operation?.Task;
                }
                if (operation is not null)
                    StartOperation(operation);
                if (task is null)
                    break;
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    completion.SetException(new TimeoutException(
                        "Controller disposal timed out while device I/O remained unsettled."));
                    return;
                }
                catch (Exception) { /* ObserveOperation records the safe, fixed fault reason. */ }
            }
            completion.SetResult();
        }
        catch (Exception error)
        {
            completion.SetException(error);
        }
    }

}
