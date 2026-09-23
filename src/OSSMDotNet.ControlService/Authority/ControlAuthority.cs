using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Authority;

/// <summary>Owns and serializes invitations, guest authority, policy, grants, and application deadlines.</summary>
/// <param name="engine">The device execution engine controlled by this authority.</param>
/// <param name="time">The clock used for grants and application deadlines.</param>
public sealed class ControlAuthority(PrototypeEngine engine, TimeProvider time) : IAsyncDisposable
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMilliseconds(750);
    private readonly object _gate = new();
    private readonly string _serviceInstanceId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<Guid, (byte[] ScopeHash, string InvitationId)> _attempts = [];
    private readonly List<IssuedGrant> _grants = [];
    private readonly Queue<long> _inputAttempts = new(10);
    private Invitation? _invitation;
    private byte[]? _invitationHash;
    private AuthoritySession? _session;
    private long _policyVersion = 1;
    private string? _epoch;
    private long _lastSequence;
    private long _lastGrantTimestamp;
    private IssuedGrant? _acceptedGrant;
    private bool _disposed;
    private long _authorityRevision;
    private long _snapshotRevision;

    /// <summary>Returns the current controller state and its required operator-action precondition.</summary>
    /// <returns>The current authority snapshot and mutation precondition.</returns>
    public ControlAuthoritySnapshot Read()
    {
        lock (_gate)
            return CreateSnapshot();
    }

    /// <summary>
    /// Creates or returns an invitation for a compatible creation request.
    /// </summary>
    /// <param name="attemptId">The idempotency identifier for this creation attempt.</param>
    /// <param name="guestOrigin">The trusted origin used in the invitation link.</param>
    /// <param name="creationScope">The creation scope bound to this attempt.</param>
    /// <returns>The reusable invitation for this scope and attempt.</returns>
    /// <exception cref="ControlRejectedException">The attempt is invalid, conflicts with a prior scope, or has been consumed.</exception>
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

            Require(_attempts.Count < 4096, "attempt-capacity");
            var result = _invitation ?? NewInvitation(origin, local);
            _attempts.Add(attemptId, (scopeHash, result.SessionId));
            return result;
        }
    }

    /// <summary>
    /// Reserves an invitation and returns a server-bound guest proof.
    /// </summary>
    /// <param name="sessionId">The invitation session identifier.</param>
    /// <param name="invitationProof">The proof presented with the invitation.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    /// <returns>The admitted session and private guest proof.</returns>
    /// <exception cref="ControlRejectedException">The invitation is invalid or another guest is already admitted.</exception>
    public JoinResult JoinGuest(string sessionId, string invitationProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            CheckAvailable();
            Require(ValidIdentifier(logicalConnectionId), "invalid-connection");
            Require(_invitation?.SessionId == sessionId && Matches(invitationProof, _invitationHash), "invitation-invalid");
            var now = time.GetTimestamp();
            ExpireSession(now);
            Require(_session is null || _session.Mode == "Ended", "session-conflict");
            var proof = RandomToken(32);
            _session = new(sessionId, now, logicalConnectionId, Hash(proof));
            _snapshotRevision++;
            return new(sessionId, logicalConnectionId, proof);
        }
    }

    /// <summary>
    /// Returns scoped guest state and its service-owned publication revision.
    /// </summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="guestProof">The private guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    /// <returns>The scoped guest state and ordering revision.</returns>
    /// <exception cref="ControlRejectedException">The guest proof or connection does not match the admitted session.</exception>
    public GuestAuthoritySnapshot ReadGuest(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestProof, logicalConnectionId);
            return new(CreateControlSnapshot(guest: true), _snapshotRevision);
        }
    }

    /// <summary>
    /// Accepts a guest input under the authority serialization gate.
    /// </summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="guestProof">The private guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    /// <param name="input">The sequenced motion request bound to the active grant and policy.</param>
    /// <exception cref="ControlRejectedException">The guest authority, grant, sequence, or desired motion is invalid.</exception>
    public void SubmitGuest(string sessionId, string guestProof, string logicalConnectionId, MotionInput input)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestProof, logicalConnectionId);
            var now = time.GetTimestamp();
            RecordInputAttempt(now);
            Require(_session!.Mode == "Approved", "session-not-approved");
            Require(engine.Snapshot.Mode == "Ready", "not-armed");
            Require(input is not null, "invalid-motion");
            Require(input!.V == 2, "unsupported-version");
            Require(input.Epoch == _epoch, "epoch-invalid");
            Require(input.PolicyVersion == _policyVersion, "policy-invalid");
            Require(input.Run, "run-required");
            Require(input.Seq is > 0 and <= 9_007_199_254_740_991 && input.Seq > _lastSequence, "sequence-invalid");
            Require(ValidDesired(input.Desired, engine.Snapshot.Limits), "desired-invalid");
            var grant = _grants.Find(candidate => candidate.Grant.Id == input.GrantId);
            Require(grant is not null && Elapsed(grant.IssuedAt, now) < GrantLifetime, "grant-invalid");
            engine.Submit(input.Desired, Deadline(grant!.IssuedAt, GrantLifetime));
            _lastSequence = input.Seq;
            _acceptedGrant = grant;
            _snapshotRevision++;
        }
    }

    /// <summary>Stops a guest session under the authority serialization gate.</summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="guestProof">The private guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    public void StopGuest(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestProof, logicalConnectionId);
            engine.Pause("guest-stop");
            InvalidateMotionAuthority();
            _snapshotRevision++;
        }
    }

    /// <summary>Ends a guest session under the authority serialization gate.</summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="guestProof">The private guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    public void LeaveGuest(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestProof, logicalConnectionId);
            EndSessionCore("guest-left");
            _snapshotRevision++;
        }
    }

    /// <summary>Revokes only the matching guest binding when its transport is lost.</summary>
    /// <param name="logicalConnectionId">The server-owned connection that was lost.</param>
    public void GuestTransportLost(string logicalConnectionId)
    {
        lock (_gate)
        {
            if (_session is not { Mode: not "Ended" } || _session.ConnectionId != logicalConnectionId)
                return;
            EndSessionCore("guest-transport-lost");
            _snapshotRevision++;
        }
    }

    /// <summary>Ends the current guest session without replacing its invitation.</summary>
    /// <param name="reason">The reason recorded for ending the session.</param>
    public void EndSession(string reason = "operator-ended")
    {
        lock (_gate)
        {
            EndSessionCore(reason);
            _snapshotRevision++;
        }
    }

    /// <summary>Returns the current reusable invitation without changing authority.</summary>
    /// <param name="guestOrigin">The trusted origin used to construct an invitation if needed.</param>
    /// <returns>The current invitation, creating one if none exists.</returns>
    public Invitation ReadInvitation(Uri guestOrigin)
    {
        ArgumentNullException.ThrowIfNull(guestOrigin);
        lock (_gate)
        {
            CheckAvailable();
            var (origin, local) = ValidateOrigin(guestOrigin);
            return _invitation ?? NewInvitation(origin, local);
        }
    }

    /// <summary>Returns one authority-consistent operator state and invitation projection.</summary>
    /// <param name="guestOrigin">The trusted origin used to construct an invitation if needed.</param>
    /// <returns>The operator authority state paired with its current invitation.</returns>
    public OperatorLiveControlStatus ReadOperatorStatus(Uri guestOrigin)
    {
        ArgumentNullException.ThrowIfNull(guestOrigin);
        lock (_gate)
        {
            CheckAvailable();
            var (origin, local) = ValidateOrigin(guestOrigin);
            return new(CreateSnapshot(), _invitation ?? NewInvitation(origin, local));
        }
    }

    /// <summary>Approves the expected pending guest admission.</summary>
    /// <param name="expected">The authority state observed before approval.</param>
    /// <param name="connectionId">The pending guest connection to approve.</param>
    /// <returns>The authority state after approval.</returns>
    public ControlAuthoritySnapshot Approve(AuthorityPrecondition expected, string connectionId) =>
        Execute(expected, () => ApproveCore(connectionId));

    /// <summary>Applies limits to the expected authority state.</summary>
    /// <param name="expected">The authority state observed before applying limits.</param>
    /// <param name="limits">The proposed speed and depth limits.</param>
    /// <returns>The authority state after applying compatible limits.</returns>
    public ControlAuthoritySnapshot ApplySessionLimits(AuthorityPrecondition expected, ControlLimits limits) =>
        Execute(expected, () =>
        {
            var before = engine.Snapshot.Limits;
            engine.SetLimits(limits);
            if (before == engine.Snapshot.Limits)
                return;
            _policyVersion++;
            _grants.Clear();
            if (engine.Snapshot.Mode == "Ready")
                IssueGrant(time.GetTimestamp());
        });

    /// <summary>Recovers the expected authority state into Paused.</summary>
    /// <param name="expected">The authority state observed before recovery.</param>
    /// <returns>The recovered authority state.</returns>
    public ControlAuthoritySnapshot Recover(AuthorityPrecondition expected) =>
        Execute(expected, engine.Recover);

    /// <summary>Arms the expected approved authority state.</summary>
    /// <param name="expected">The approved authority state observed before arming.</param>
    /// <returns>The authority state after arming.</returns>
    public ControlAuthoritySnapshot Arm(AuthorityPrecondition expected) =>
        Execute(expected, ArmCore);

    /// <summary>Recovers, approves, and arms the expected guest authority in one serialized operation.</summary>
    /// <param name="expected">The authority state observed before allowing control.</param>
    /// <returns>The authority state after the combined operation.</returns>
    public ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected) =>
        Execute(expected, () =>
        {
            var current = engine.Snapshot;
            if (current.Mode == "Initializing")
                engine.Recover();
            if (_session is { Mode: "PendingApproval", ConnectionId: { } connectionId })
                ApproveCore(connectionId);
            ArmCore();
        });

    /// <summary>Stops the expected authority state and advances its revision even when already paused.</summary>
    /// <returns>The authority state after stopping motion.</returns>
    public ControlAuthoritySnapshot Stop()
    {
        lock (_gate)
        {
            engine.Pause();
            InvalidateMotionAuthority();
            _authorityRevision++;
            _snapshotRevision++;
            return CreateSnapshot();
        }
    }

    /// <summary>Ends the expected session and replaces its invitation.</summary>
    /// <param name="expected">The authority state observed before ending the session.</param>
    /// <param name="guestOrigin">The trusted origin of the replacement invitation.</param>
    /// <returns>The authority state after ending the session.</returns>
    public ControlAuthoritySnapshot End(AuthorityPrecondition expected, Uri guestOrigin) =>
        Cycle(expected, guestOrigin);

    /// <summary>Cycles the expected invitation and revokes guest authority.</summary>
    /// <param name="expected">The authority state observed before cycling the invitation.</param>
    /// <param name="guestOrigin">The trusted origin of the replacement invitation.</param>
    /// <returns>The authority state after cycling the invitation.</returns>
    public ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected, Uri guestOrigin) =>
        Cycle(expected, guestOrigin);

    /// <summary>Unlocks the expected external-lock revision.</summary>
    /// <param name="expected">The authority state observed before unlocking.</param>
    /// <param name="lockRevision">The external-lock revision to clear.</param>
    /// <returns>The authority state after unlocking.</returns>
    public ControlAuthoritySnapshot Unlock(AuthorityPrecondition expected, long lockRevision) =>
        Execute(expected, () => engine.Unlock(lockRevision));

    /// <summary>Processes deadlines, feedback freshness, and queued device work under the authority gate.</summary>
    public void Tick()
    {
        ControlSnapshot before;
        lock (_gate)
        {
            before = CreateControlSnapshot(guest: false);
            var now = time.GetTimestamp();
            ExpireSession(now);
        }

        engine.Tick();

        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (engine.Snapshot.Mode == "Ready")
            {
                if (_acceptedGrant is not null && Elapsed(_acceptedGrant.IssuedAt, now) >= GrantLifetime)
                {
                    engine.Pause("grant-expired");
                    InvalidateMotionAuthority();
                }
                else if (Elapsed(_lastGrantTimestamp, now) >= TimeSpan.FromMilliseconds(250))
                {
                    IssueGrant(now);
                }
            }
            else if (_epoch is not null)
            {
                InvalidateMotionAuthority();
            }

            if (before != CreateControlSnapshot(guest: false))
                _snapshotRevision++;
        }
    }

    /// <summary>Stops authority during host shutdown without a caller-provided precondition.</summary>
    public void StopForShutdown()
    {
        lock (_gate)
        {
            engine.Pause("control-service-stopping");
            InvalidateMotionAuthority();
            _authorityRevision++;
            _snapshotRevision++;
        }
    }

    /// <summary>Counts a connection-bound input attempt rejected before model conversion.</summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="guestProof">The private guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    internal void RecordRejectedGuestInputAttempt(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            AuthenticateGuest(sessionId, guestProof, logicalConnectionId);
            RecordInputAttempt(time.GetTimestamp());
        }
    }

    private ControlAuthoritySnapshot Cycle(AuthorityPrecondition expected, Uri guestOrigin)
    {
        ArgumentNullException.ThrowIfNull(guestOrigin);
        var (origin, local) = ValidateOrigin(guestOrigin);
        return Execute(expected, () =>
        {
            EndSessionCore("invitation-cycled");
            NewInvitation(origin, local);
        });
    }

    private void ApproveCore(string connectionId)
    {
        Require(_session?.Mode == "PendingApproval" && _session.ConnectionId == connectionId, "approval-invalid");
        if (Elapsed(_session!.StartedAt, time.GetTimestamp()) >= TimeSpan.FromSeconds(60))
        {
            EndSessionCore("approval-expired");
            throw Rejection("approval-expired");
        }
        _session.Mode = "Approved";
        _session.StartedAt = time.GetTimestamp();
    }

    private void ArmCore()
    {
        ExpireSession(time.GetTimestamp());
        Require(_session is { Mode: "Approved" }, "session-not-approved");
        engine.Arm();
        _epoch = RandomToken(16);
        _lastSequence = 0;
        _inputAttempts.Clear();
        _acceptedGrant = null;
        _grants.Clear();
        IssueGrant(time.GetTimestamp());
    }

    private void AuthenticateGuest(string sessionId, string proof, string connectionId)
    {
        Require(_session is { Mode: "PendingApproval" or "Approved" } && _session.Id == sessionId &&
            _session.ConnectionId == connectionId && Matches(proof, _session.GuestHash), "guest-unauthorized");
        ExpireSession(time.GetTimestamp());
        Require(_session!.Mode != "Ended", "session-expired");
    }

    private void ExpireSession(long now)
    {
        if (_session is null || _session.Mode == "Ended")
            return;
        var lifetime = _session.Mode == "PendingApproval" ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(30);
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
        engine.Pause(reason);
        InvalidateMotionAuthority();
    }

    private void InvalidateMotionAuthority()
    {
        _epoch = null;
        _grants.Clear();
        _acceptedGrant = null;
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

    private void RecordInputAttempt(long now)
    {
        while (_inputAttempts.TryPeek(out var attemptedAt) && Elapsed(attemptedAt, now) >= TimeSpan.FromSeconds(1))
            _inputAttempts.Dequeue();
        Require(_inputAttempts.Count < 10, "rate-limited");
        _inputAttempts.Enqueue(now);
    }

    private ControlAuthoritySnapshot Execute(AuthorityPrecondition expected, Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            Validate(expected, CreateControlSnapshot(guest: false));
            operation();
            _authorityRevision++;
            _snapshotRevision++;
            return CreateSnapshot();
        }
    }

    private ControlAuthoritySnapshot CreateSnapshot()
    {
        var snapshot = CreateControlSnapshot(guest: false);
        return new(snapshot, new(_serviceInstanceId, _authorityRevision,
            new(snapshot.SessionId, snapshot.ConnectionId, snapshot.SessionMode)), _snapshotRevision);
    }

    private ControlSnapshot CreateControlSnapshot(bool guest)
    {
        var execution = engine.Snapshot;
        return new(execution.Mode, execution.Reason, execution.Generation, execution.LockRevision,
            execution.BridgeReady, execution.DeviceMoving, execution.Desired, guest ? null : execution.Observed,
            execution.Limits, _policyVersion, _session?.Mode ?? "None", _session?.Id,
            guest ? null : _session?.ConnectionId, _lastSequence, PublishedGrant(time.GetTimestamp()))
        {
            OperationPending = execution.OperationPending,
            Position = execution.Position,
            Velocity = execution.Velocity,
            Current = execution.Current,
            VerifiedSeq = execution.DesiredConfirmed ? _lastSequence : 0,
            DeviceEnabled = execution.DeviceEnabled,
            IsSimulator = execution.IsSimulator
        };
    }

    private MotionGrant? PublishedGrant(long now)
    {
        if (engine.Snapshot.Mode != "Ready" || _session?.Mode != "Approved")
            return null;
        for (var index = _grants.Count - 1; index >= 0; index--)
        {
            var grant = _grants[index];
            if (Elapsed(grant.IssuedAt, now) < GrantLifetime)
            {
                return grant.Grant with
                {
                    ValidForMs = Math.Max(0,
                        (int)Math.Ceiling((GrantLifetime - Elapsed(grant.IssuedAt, now)).TotalMilliseconds))
                };
            }
        }
        return null;
    }

    private void Validate(AuthorityPrecondition? expected, ControlSnapshot current)
    {
        if (expected is null)
            throw new ControlRejectedException("authority-precondition-required");
        if (!string.Equals(expected.ServiceInstanceId, _serviceInstanceId, StringComparison.Ordinal))
            throw new ControlRejectedException("service-instance-mismatch");
        if (expected.AuthorityRevision != _authorityRevision)
            throw new ControlRejectedException("authority-revision-mismatch");
        if (expected.GuestAdmission != new GuestAdmission(current.SessionId, current.ConnectionId, current.SessionMode))
            throw new ControlRejectedException("guest-admission-mismatch");
    }

    private Invitation NewInvitation(string origin, bool local)
    {
        var id = RandomToken(16);
        var secret = RandomToken(32);
        _invitationHash = Hash(secret);
        return _invitation = new(id, $"{origin}/join/{id}#invite={secret}", null, local);
    }

    private static bool ValidDesired(MotionSettings? desired, ControlLimits limits) =>
        desired is
        {
            Pattern: MotionPattern.SimpleStroke,
            Sensation: 50,
            Speed: >= 0 and <= 100,
            Stroke: >= 0 and <= 100,
            Depth: >= 0 and <= 100
        } && desired.Stroke <= desired.Depth && desired.Depth <= limits.MaximumDepth &&
        desired.Speed <= limits.SpeedCap && (desired.Speed == 0 || desired.Stroke > 0);

    private long Deadline(long start, TimeSpan duration) =>
        checked(start + (long)Math.Ceiling(duration.TotalSeconds * time.TimestampFrequency));

    private TimeSpan Elapsed(long start, long end) => time.GetElapsedTime(start, end);

    private static byte[] Hash(string value)
    {
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

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

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

    private static (string Origin, bool Local) ValidateOrigin(Uri? uri)
    {
        Require(uri is { IsAbsoluteUri: true } && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0, "origin-invalid");
        var authority = uri!.GetLeftPart(UriPartial.Authority);
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
        Require(uri.Scheme == Uri.UriSchemeHttps ||
            (uri.Scheme == Uri.UriSchemeHttp && local), "origin-invalid");
        return (authority, local);
    }

    private static ControlRejectedException Rejection(string code) => new(code);

    private void CheckAvailable() => Require(!_disposed, "engine-disposed");

    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw Rejection(code);
    }

    /// <summary>Revokes application authority and disposes the execution core.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _invitation = null;
            _invitationHash = null;
            if (_session is not null)
            {
                _session.Mode = "Ended";
                _session.GuestHash = null;
                _session.ConnectionId = null;
            }
            InvalidateMotionAuthority();
        }
        await engine.DisposeAsync();
    }
}
