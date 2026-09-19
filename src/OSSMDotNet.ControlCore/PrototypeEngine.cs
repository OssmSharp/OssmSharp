using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore;

/// <summary>Coordinates invitations, guest authority, safety policy, and device commands.</summary>
public sealed class PrototypeEngine : IAsyncDisposable
{
    private const string SimulatorProfile = "ossm-rs-sim-v1";
    private static readonly TimeSpan FeedbackFreshness = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMilliseconds(750);
    private readonly object _gate = new();
    private readonly IDeviceBridge _bridge;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, (byte[] ScopeHash, string InvitationId)> _attempts = [];
    private readonly List<IssuedGrant> _grants = [];
    private Invitation? _invitation;
    private byte[]? _invitationHash;
    private Session? _session;
    private DeviceState? _observed;
    private long _sampleTimestamp;
    private long _connection;
    private bool _connected;
    private bool _hasSample;
    private string _mode = "Initializing";
    private string _reason = "awaiting-feedback";
    private bool _locked;
    private long _lockRevision;
    private long _generation;
    private ControlLimits _limits = new(100, 100);
    private long _policyVersion = 1;
    private string? _epoch;
    private long _lastSequence;
    private long _lastGrantTimestamp;
    private IssuedGrant? _authority;
    private double _tokens = 20;
    private long _tokenTimestamp;
    private MotionSettings _desired = MotionSettings.Stopped;
    private bool _hasDesired;
    private bool _envelopeStopped;
    private MotionSettings? _zeroAwaitingEcho;
    private bool _stopRequested;
    private bool _stopQueued;
    private StopFeedback? _stopFeedback;
    private Operation? _operation;
    private OwnedCommand? _lastOwnedCommand;
    private int _unobservedCommandSpeed;
    private int _unobservedCommandDepth;
    private long _commandId;
    private Task? _disposeTask;

    /// <summary>Creates the admin-owned controller with its device boundary and monotonic clock.</summary>
    public PrototypeEngine(IDeviceBridge bridge, TimeProvider timeProvider)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _bridge.SampleReceived += OnSample;
    }

    /// <summary>Gets the current controller state for trusted admin use.</summary>
    public ControlSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return MakeSnapshot(_time.GetTimestamp(), guest: false);
        }
    }

    /// <summary>Returns the current reusable invitation for the validated guest origin.</summary>
    public Invitation GetInvitation(Uri guestOrigin)
    {
        lock (_gate)
        {
            CheckAvailable();
            var (origin, local) = ValidateOrigin(guestOrigin);
            return _invitation ?? NewInvitation(origin, local);
        }
    }

    /// <summary>Replaces the invitation and revokes its active guest authority.</summary>
    public Invitation CycleInvitation(Uri guestOrigin)
    {
        lock (_gate)
        {
            CheckAvailable();
            var (origin, local) = ValidateOrigin(guestOrigin);
            EndSessionCore("invitation-cycled");
            return NewInvitation(origin, local);
        }
    }

    private Invitation NewInvitation(string origin, bool local)
    {
        var id = RandomToken(16);
        var secret = RandomToken(32);
        _invitationHash = Hash(secret);
        return _invitation = new(id, $"{origin}/join/{id}#invite={secret}", null, local);
    }

    /// <summary>Creates or returns an invitation for a compatible creation request.</summary>
    public Invitation CreateInvitation(Guid attemptId, Uri guestOrigin, string creationScope)
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(attemptId != Guid.Empty && ValidIdentifier(creationScope), "invalid-attempt");
            var (origin, local) = ValidateOrigin(guestOrigin);
            var scopeHash = Hash(creationScope);
            if (_attempts.TryGetValue(attemptId, out var owner))
            {
                Require(CryptographicOperations.FixedTimeEquals(owner.ScopeHash, scopeHash), "attempt-conflict");
                Require(_invitation?.SessionId == owner.InvitationId, "attempt-consumed");
                return _invitation!;
            }

            // Compatible retries retain their original invitation and do not restore one invalidated by CycleInvitation.
            Require(_attempts.Count < 4096, "attempt-capacity");
            var result = _invitation ?? NewInvitation(origin, local);
            _attempts.Add(attemptId, (scopeHash, result.SessionId));
            return result;
        }
    }

    /// <summary>Reserves the guest slot and establishes a connection-bound proof.</summary>
    public JoinResult Join(string sessionId, string invitationSecret, string connectionId)
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(ValidIdentifier(connectionId), "invalid-connection");
            Require(_invitation?.SessionId == sessionId &&
                Matches(invitationSecret, _invitationHash), "invitation-invalid");
            var now = _time.GetTimestamp();
            ExpireSession(now);
            Require(_session is null || _session.Mode == "Ended", "session-conflict");

            var token = RandomToken(32);
            _session = new Session(sessionId, now, connectionId, Hash(token));
            return new(sessionId, connectionId, token);
        }
    }

    /// <summary>Approves a waiting guest connection after device safety checks succeed.</summary>
    public void Approve(string connectionId)
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(_session?.Mode == "PendingApproval" && _session.ConnectionId == connectionId,
                "approval-invalid");
            if (Elapsed(_session!.StartedAt, _time.GetTimestamp()) >= TimeSpan.FromSeconds(60))
            {
                EndSessionCore("approval-expired");
                throw Rejection("approval-expired");
            }

            _session.Mode = "Approved";
            _session.StartedAt = _time.GetTimestamp();
        }
    }

    /// <summary>Applies compatible limits atomically without rearming control.</summary>
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
            ExpireSession(now);
            if (_mode == "Ready" && _authority is not null &&
                Elapsed(_authority.IssuedAt, now) >= GrantLifetime)
                PauseCore("grant-expired");

            _limits = limits!;
            _policyVersion++;
            _grants.Clear();
            if (_mode == "Ready")
                IssueGrant(now);
            // Keep the accepted authority's original issuance deadline. A grant for a
            // new policy is not guest input, so it must not renew that lease.
        }
    }

    private bool FitsProposedLimits(ControlLimits limits)
    {
        if (_unobservedCommandSpeed > limits.SpeedCap || _unobservedCommandDepth > limits.MaximumDepth)
            return false;
        if ((_authority is not null || _hasDesired) && !ValidDesired(_desired, limits))
            return false;
        if (_observed is { } observed &&
            (observed.Enabled || observed.Moving || observed.Desired.Speed > 0) &&
            !ValidDesired(observed.Desired, limits))
            return false;
        if (_operation is { Kind: not OperationKind.Stop } operation && !ValidDesired(operation.Desired, limits))
            return false;
        return _zeroAwaitingEcho is null || ValidDesired(_zeroAwaitingEcho, limits);
    }

    /// <summary>Arms the approved session when policy and device safety checks succeed.</summary>
    public void Arm()
    {
        lock (_gate)
        {
            CheckAvailable();
            var now = _time.GetTimestamp();
            ExpireSession(now);
            Require(!_locked, "external-lock");
            Require(_mode == "Paused", "not-paused");
            Require(_session?.Mode == "Approved", "session-not-approved");
            Require(PolicyAllowsArming(), "limits-not-configured");
            Require(KnownStopped(now), "device-not-stopped");
            Require(NoOutstandingIo(), "operation-pending");
            _generation++;
            _mode = "Ready";
            _reason = "armed";
            _epoch = RandomToken(16);
            _lastSequence = 0;
            _tokens = 20;
            _tokenTimestamp = now;
            _authority = null;
            _hasDesired = false;
            _envelopeStopped = false;
            _zeroAwaitingEcho = null;
            _desired = _observed!.Desired with { Speed = 0 };
            _stopRequested = false;
            _grants.Clear();
            IssueGrant(now);
        }
    }

    /// <summary>Pauses control and invalidates active guest motion authority.</summary>
    public void Pause(string reason = "operator-stop")
    {
        lock (_gate)
        {
            CheckAvailable();
            PauseCore(reason);
        }
    }

    /// <summary>Ends the guest session without replacing the invitation.</summary>
    public void EndSession(string reason = "operator-ended")
    {
        lock (_gate)
        {
            CheckAvailable();
            EndSessionCore(reason);
        }
    }

    /// <summary>Clears a matching external lock and returns the controller to Paused.</summary>
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

    /// <summary>Returns the guest-safe snapshot for an authenticated connection.</summary>
    public ControlSnapshot GuestSnapshot(string sessionId, string guestToken, string connectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestToken, connectionId);
            return MakeSnapshot(_time.GetTimestamp(), guest: true);
        }
    }

    /// <summary>Validates and accepts one authenticated guest motion input.</summary>
    public void Submit(string sessionId, string guestToken, string connectionId, MotionInput input)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestToken, connectionId);
            var now = _time.GetTimestamp();
            _tokens = Math.Min(20, _tokens + Elapsed(_tokenTimestamp, now).TotalSeconds * 10);
            _tokenTimestamp = now;
            Require(_tokens >= 1, "rate-limited");
            _tokens--;
            Require(_session!.Mode == "Approved", "session-not-approved");
            if (_authority is not null && Elapsed(_authority.IssuedAt, now) >= GrantLifetime)
                PauseCore("grant-expired");
            Require(_mode == "Ready" && !_locked, "not-armed");
            Require(input is not null, "invalid-motion");
            Require(input!.V == 2, "unsupported-version");
            Require(input.Epoch == _epoch, "epoch-invalid");
            Require(input.PolicyVersion == _policyVersion, "policy-invalid");
            Require(input.Run, "run-required");
            Require(input.Seq is > 0 and <= 9007199254740991 && input.Seq > _lastSequence, "sequence-invalid");
            Require(ValidDesired(input.Desired), "desired-invalid");
            IssuedGrant? grant = null;
            foreach (var candidate in _grants)
            {
                if (candidate.Grant.Id == input.GrantId)
                {
                    grant = candidate;
                    break;
                }
            }
            Require(grant is not null && Elapsed(grant.IssuedAt, now) < GrantLifetime, "grant-invalid");
            Require(Fresh(now), "feedback-stale");
            _lastSequence = input.Seq;
            _authority = grant;
            _desired = input.Desired;
            _hasDesired = true;
        }
    }

    /// <summary>Stops motion for an authenticated guest connection.</summary>
    public void GuestStop(string sessionId, string guestToken, string connectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestToken, connectionId);
            PauseCore("guest-stop");
        }
    }

    /// <summary>Ends the authenticated guest session without replacing the invitation.</summary>
    public void Leave(string sessionId, string guestToken, string connectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestToken, connectionId);
            EndSessionCore("guest-left");
        }
    }

    /// <summary>Revokes guest authority when its bound transport connection is lost.</summary>
    public void TransportLost(string connectionId)
    {
        lock (_gate)
        {
            CheckAvailable();
            if (_session is { Mode: not "Ended" } && _session.ConnectionId == connectionId)
                EndSessionCore("guest-transport-lost");
        }
    }

    /// <summary>Processes deadlines, feedback freshness, and queued device work.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return;
            var now = _time.GetTimestamp();
            ObserveOperation(now);
            ExpireSession(now);
            if (_hasSample && _connected && !Fresh(now))
                Fault("feedback-stale");
            ObserveStopFeedback(now);
            if (_mode == "Ready")
            {
                if (_authority is not null && Elapsed(_authority.IssuedAt, now) >= GrantLifetime)
                    PauseCore("grant-expired");
                else if (Elapsed(_lastGrantTimestamp, now) >= TimeSpan.FromMilliseconds(250))
                    IssueGrant(now);
            }

            DispatchNext(now);
        }
    }

    private void AuthenticateGuest(string sessionId, string token, string connectionId)
    {
        CheckAvailable();
        // Authentication precedes expiry processing: a bad proof cannot stop another guest.
        Require(_session is { Mode: "PendingApproval" or "Approved" } && _session.Id == sessionId &&
            _session.ConnectionId == connectionId && Matches(token, _session.GuestHash), "guest-unauthorized");
        ExpireSession(_time.GetTimestamp());
        Require(_session!.Mode != "Ended", "session-expired");
    }

    private void ExpireSession(long now)
    {
        if (_session is null || _session.Mode == "Ended")
            return;
        var lifetime = _session.Mode switch
        {
            "PendingApproval" => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromMinutes(30)
        };
        if (Elapsed(_session.StartedAt, now) >= lifetime)
            EndSessionCore("session-expired");
    }

    private void EndSessionCore(string reason)
    {
        if (_session is not null)
        {
            _session.Mode = "Ended";
            _session.GuestHash = null;
            _session.ConnectionId = null;
        }
        PauseCore(reason);
    }

    private void PauseCore(string reason)
    {
        if (!_stopRequested || _epoch is not null)
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
        _epoch = null;
        _grants.Clear();
        _authority = null;
        _hasDesired = false;
        _envelopeStopped = false;
        _zeroAwaitingEcho = null;
        _desired = (_observed?.Desired ?? MotionSettings.Stopped) with { Speed = 0 };
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

    private void OnSample(DeviceState sample)
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return;
            var now = _time.GetTimestamp();
            if (!sample.Connected || sample.ProfileId != SimulatorProfile)
            {
                _connected = false;
                _hasSample = false;
                _observed = sample;
                _lastOwnedCommand = null;
                Fault(sample.Connected ? "unsupported-profile" : "bridge-disconnected");
                return;
            }

            var reconnecting = !_connected;
            if (!reconnecting && _observed is not null && sample.ObservationSequenceNumber <= _observed.ObservationSequenceNumber)
                return;
            if (sample.ObservationSequenceNumber < 0 || sample.ChangeRevision < 0 || sample.Desired is null)
            {
                Fault("invalid-feedback");
                return;
            }

            var previous = _observed;
            if (reconnecting)
            {
                _connection++;
                _lastOwnedCommand = null;
            }
            _connected = true;
            _hasSample = true;
            _sampleTimestamp = now;
            _observed = sample;
            if (OwnedEcho(sample))
            {
                _unobservedCommandSpeed = 0;
                _unobservedCommandDepth = 0;
            }
            var parametersChanged = previous is not null &&
                (previous.Desired != sample.Desired || (!reconnecting && sample.ChangeRevision > previous.ChangeRevision));
            if (parametersChanged && !OwnedEcho(sample))
                ExternalChange();
            else if (!reconnecting && previous is not null &&
                (sample.ChangeRevision < previous.ChangeRevision ||
                 (sample.Desired != previous.Desired && sample.ChangeRevision == previous.ChangeRevision)))
                Fault("invalid-feedback");
            else if (reconnecting && !KnownStopped(now))
                Fault("connection-not-stopped");
        }
    }

    private bool OwnedEcho(DeviceState sample) =>
        _lastOwnedCommand is { } own && own.Connection == _connection &&
        sample.CommandId == own.Id && sample.Desired == own.Desired;

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
            _observed!.ObservationSequenceNumber > feedback.SampleSequence)
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

    private void DispatchNext(long now)
    {
        if (_operation is not null)
            return;
        if (_stopQueued)
        {
            _stopQueued = false;
            _stopFeedback = null;
            Dispatch(OperationKind.Stop, (_observed?.Desired ?? MotionSettings.Stopped) with { Speed = 0 }, now);
            return;
        }
        if (_stopFeedback is not null || _mode != "Ready" || !_hasDesired || !MotionAuthorized(now))
            return;

        if (_envelopeStopped)
        {
            if (_zeroAwaitingEcho is null)
            {
                _zeroAwaitingEcho = _desired with { Speed = 0 };
                Dispatch(OperationKind.Zero, _zeroAwaitingEcho, now);
                return;
            }
            if (_observed!.Desired != _zeroAwaitingEcho || !KnownStopped(now))
                return;
            if (!SameEnvelope(_desired, _zeroAwaitingEcho))
            {
                _zeroAwaitingEcho = null;
                return;
            }
            _envelopeStopped = false;
            _zeroAwaitingEcho = null;
        }

        if (!SameEnvelope(_observed!.Desired, _desired))
            Dispatch(OperationKind.Stop, _observed.Desired with { Speed = 0 }, now, forEnvelope: true);
        else if (_desired != _observed.Desired)
            Dispatch(OperationKind.Motion, _desired, now);
        else
            _hasDesired = false;
    }

    private bool MotionAuthorized(long now)
    {
        if (_mode != "Ready" || _locked || _epoch is null || _authority is null)
            return false;
        if (Elapsed(_authority.IssuedAt, now) >= GrantLifetime)
        {
            PauseCore("grant-expired");
            return false;
        }
        if (!Fresh(now))
        {
            Fault("feedback-stale");
            return false;
        }
        return _session?.Mode == "Approved" && ValidDesired(_desired);
    }

    private void Dispatch(OperationKind kind, MotionSettings desired, long now, bool forEnvelope = false)
    {
        // Dispatch and the final authority check run under the same lock as Pause and Lock.
        // The bridge must return a Task promptly; that Task owns the writer until settlement.
        if (kind != OperationKind.Stop && !MotionAuthorized(now))
            return;
        var operation = new Operation(++_commandId, kind, desired, now, _generation,
            _observed?.ObservationSequenceNumber ?? -1, _connection, forEnvelope);
        _operation = operation;
        _lastOwnedCommand = new(operation.Id, desired, _connection);
        if (kind != OperationKind.Stop)
        {
            // Completion need not include feedback. Retain bounded maxima until an
            // owned echo establishes the current tuple, even across replacement I/O.
            _unobservedCommandSpeed = Math.Max(_unobservedCommandSpeed, desired.Speed);
            _unobservedCommandDepth = Math.Max(_unobservedCommandDepth, desired.Depth);
        }
        try
        {
            operation.Task = kind == OperationKind.Stop
                ? _bridge.SetMotionAsync(operation.Id, MotionSettings.Stopped, CancellationToken.None)
                : _bridge.SetMotionAsync(operation.Id, desired, CancellationToken.None);
        }
        catch (Exception error)
        {
            operation.Task = Task.FromException(error);
        }
        _ = operation.Task.ContinueWith(task =>
        {
            var completedAt = _time.GetTimestamp();
            _ = task.Exception;
            lock (_gate)
                operation.CompletedAt ??= completedAt;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void IssueGrant(long now)
    {
        for (var index = _grants.Count - 1; index >= 0; index--)
        {
            if (Elapsed(_grants[index].IssuedAt, now) >= GrantLifetime)
                _grants.RemoveAt(index);
        }
        _grants.Add(new(new(RandomToken(16), _epoch!, _policyVersion, 750), now));
        _lastGrantTimestamp = now;
    }

    private ControlSnapshot MakeSnapshot(long now, bool guest)
    {
        return new(_mode, _reason, _generation, _lockRevision, Fresh(now), _observed?.Moving ?? false,
            _desired, guest ? null : _observed, _limits, _policyVersion, _session?.Mode ?? "None",
            _session?.Id, guest ? null : _session?.ConnectionId, _lastSequence, PublishedGrant(now))
        {
            OperationPending = !NoOutstandingIo(),
            DeviceEnabled = _observed?.Enabled == true,
            IsSimulator = _observed?.ProfileId == SimulatorProfile,
            Position = Fresh(now) ? _observed?.Position : null,
            Velocity = Fresh(now) ? _observed?.Velocity : null,
            Current = Fresh(now) ? _observed?.Desired : null,
            VerifiedSeq = Fresh(now) && _lastSequence > 0 && !_hasDesired && NoOutstandingIo() &&
                _observed!.Desired == _desired && OwnedEcho(_observed) ? _lastSequence : 0
        };
    }

    private MotionGrant? PublishedGrant(long now)
    {
        for (var index = _grants.Count - 1; index >= 0; index--)
        {
            var grant = _grants[index];
            if (Elapsed(grant.IssuedAt, now) < GrantLifetime)
            {
                return grant.Grant with
                {
                    ValidForMs = Math.Max(0, (int)Math.Ceiling((GrantLifetime - Elapsed(grant.IssuedAt, now)).TotalMilliseconds))
                };
            }
        }

        return null;
    }

    private bool Fresh(long now) => _hasSample && _connected && _observed?.ProfileId == SimulatorProfile &&
        Elapsed(_sampleTimestamp, now) < FeedbackFreshness;

    private bool KnownStopped(long now) => Fresh(now) &&
        _observed is { Enabled: false, Moving: false, Desired.Speed: 0 };

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

    private TimeSpan Elapsed(long start, long end) => _time.GetElapsedTime(start, end);
    private static byte[] Hash(string value)
    {
        // Callers pass a validated identifier (at most 256 chars) or a generated token.
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(256)];
        var length = Encoding.UTF8.GetBytes(value.AsSpan(), utf8);
        var hash = SHA256.HashData(utf8[..length]);
        CryptographicOperations.ZeroMemory(utf8[..length]);
        return hash;
    }

    private static string RandomToken(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        var token = Base64Url.EncodeToString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return token;
    }

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool Matches(string? proof, ReadOnlySpan<byte> expectedHash)
    {
        if (proof is not { Length: 43 } || expectedHash.Length != SHA256.HashSizeInBytes)
            return false;
        Span<byte> bytes = stackalloc byte[43];
        for (var index = 0; index < proof.Length; index++)
        {
            var character = proof[index];
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                CryptographicOperations.ZeroMemory(bytes);
                return false;
            }
            bytes[index] = (byte)character;
        }
        Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, actualHash);
        var matches = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        CryptographicOperations.ZeroMemory(bytes);
        CryptographicOperations.ZeroMemory(actualHash);
        return matches;
    }

    private static ControlRejectedException Rejection(string code) => new(code);
    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw Rejection(code);
    }
    private void CheckAvailable() => Require(_disposeTask is null, "engine-disposed");

    private static (string Origin, bool Local) ValidateOrigin(Uri? uri)
    {
        Require(uri is { IsAbsoluteUri: true } && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0, "origin-invalid");
        var authority = uri!.GetLeftPart(UriPartial.Authority);
        // Inspect the original spelling: Uri canonicalizes dot segments, default ports,
        // backslashes, and abbreviated IP addresses before exposing its components.
        var raw = uri.OriginalString;
        var prefix = uri.Scheme + "://";
        Require(raw.Length <= 2048 && raw == raw.Trim() && !raw.Contains('\\') &&
            raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "origin-invalid");
        var slash = raw.IndexOf('/', prefix.Length);
        Require(slash < 0 || slash == raw.Length - 1, "origin-invalid");
        var rawAuthority = raw[prefix.Length..].TrimEnd('/');
        var rawHost = rawAuthority.StartsWith('[')
            ? rawAuthority[..(rawAuthority.IndexOf(']') + 1)]
            : rawAuthority.Split(':', 2)[0];
        var local = rawHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            rawHost == "127.0.0.1" || rawHost == "[::1]";
        Require(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && local), "origin-invalid");
        return (authority, local);
    }

    /// <summary>Stops the controller and releases its device bridge resources.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new(_disposeTask);
            EndSessionCore("disposed");
            _invitation = null;
            _invitationHash = null;
            _bridge.SampleReceived -= OnSample;
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
                lock (_gate)
                {
                    ObserveOperation(_time.GetTimestamp());
                    if (_operation is null && _stopQueued)
                        DispatchNext(_time.GetTimestamp());
                    task = _operation?.Task;
                }
                if (task is null)
                    break;
                try { await task.ConfigureAwait(false); }
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
