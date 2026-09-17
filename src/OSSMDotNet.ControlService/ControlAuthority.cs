using OSSMDotNet.ControlCore;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Serializes operator actions and verifies their expected authority state immediately before execution.</summary>
public sealed class ControlAuthority(PrototypeEngine engine)
{
    private readonly object _gate = new();
    private readonly string _serviceInstanceId = Guid.NewGuid().ToString("N");
    private long _authorityRevision;
    private long _snapshotRevision;

    /// <summary>Returns the current controller state and its required operator-action precondition.</summary>
    public ControlAuthoritySnapshot Read()
    {
        lock (_gate)
            return CreateSnapshot();
    }

    /// <summary>Reserves an invitation and returns a server-bound guest proof.</summary>
    public JoinResult JoinGuest(string sessionId, string invitationProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            var joined = engine.Join(sessionId, invitationProof, logicalConnectionId);
            _snapshotRevision++;
            return joined;
        }
    }

    /// <summary>Returns scoped guest state and its service-owned publication revision.</summary>
    public GuestAuthoritySnapshot ReadGuest(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
            return new(engine.GuestSnapshot(sessionId, guestProof, logicalConnectionId), _snapshotRevision);
    }

    /// <summary>Accepts a guest input under the authority serialization gate.</summary>
    public void SubmitGuest(string sessionId, string guestProof, string logicalConnectionId, MotionInput input)
    {
        lock (_gate)
        {
            engine.Submit(sessionId, guestProof, logicalConnectionId, input);
            _snapshotRevision++;
        }
    }

    /// <summary>Stops a guest session under the authority serialization gate.</summary>
    public void StopGuest(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            engine.GuestStop(sessionId, guestProof, logicalConnectionId);
            _snapshotRevision++;
        }
    }

    /// <summary>Ends a guest session under the authority serialization gate.</summary>
    public void LeaveGuest(string sessionId, string guestProof, string logicalConnectionId)
    {
        lock (_gate)
        {
            engine.Leave(sessionId, guestProof, logicalConnectionId);
            _snapshotRevision++;
        }
    }

    /// <summary>Revokes only the matching guest binding when its transport is lost.</summary>
    public void GuestTransportLost(string logicalConnectionId)
    {
        lock (_gate)
        {
            engine.TransportLost(logicalConnectionId);
            _snapshotRevision++;
        }
    }

    /// <summary>Returns the current reusable invitation without changing authority.</summary>
    public Invitation ReadInvitation(Uri guestOrigin)
    {
        ArgumentNullException.ThrowIfNull(guestOrigin);
        lock (_gate)
            return engine.GetInvitation(guestOrigin);
    }

    /// <summary>Returns one authority-consistent operator state and invitation projection.</summary>
    public OperatorLiveControlStatus ReadOperatorStatus(Uri guestOrigin)
    {
        ArgumentNullException.ThrowIfNull(guestOrigin);
        lock (_gate)
        {
            var invitation = engine.GetInvitation(guestOrigin);
            return new(CreateSnapshot(), invitation);
        }
    }

    /// <summary>Approves the expected pending guest admission.</summary>
    public ControlAuthoritySnapshot Approve(AuthorityPrecondition expected, string connectionId) =>
        Execute(expected, () => engine.Approve(connectionId));

    /// <summary>Applies limits to the expected authority state.</summary>
    public ControlAuthoritySnapshot ApplySessionLimits(AuthorityPrecondition expected, ControlLimits limits) =>
        Execute(expected, () => engine.SetLimits(limits));

    /// <summary>Recovers the expected authority state into Paused.</summary>
    public ControlAuthoritySnapshot Recover(AuthorityPrecondition expected) =>
        Execute(expected, engine.Recover);

    /// <summary>Arms the expected approved authority state.</summary>
    public ControlAuthoritySnapshot Arm(AuthorityPrecondition expected) =>
        Execute(expected, engine.Arm);

    /// <summary>Recovers, approves, and arms the expected guest authority in one serialized operation.</summary>
    public ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected) =>
        Execute(expected, () =>
        {
            var current = engine.Snapshot;
            if (current.Mode == "Initializing")
                engine.Recover();
            if (current.SessionMode == "PendingApproval" && current.ConnectionId is { } connectionId)
                engine.Approve(connectionId);
            engine.Arm();
        });

    /// <summary>Stops the expected authority state and advances its revision even when already paused.</summary>
    public ControlAuthoritySnapshot Stop()
    {
        lock (_gate)
        {
            engine.Pause();
            _authorityRevision++;
            _snapshotRevision++;
            return CreateSnapshot();
        }
    }

    /// <summary>Ends the expected session and replaces its invitation.</summary>
    public ControlAuthoritySnapshot End(AuthorityPrecondition expected, Uri guestOrigin) =>
        Execute(expected, () => engine.CycleInvitation(guestOrigin));

    /// <summary>Cycles the expected invitation and revokes guest authority.</summary>
    public ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected, Uri guestOrigin) =>
        Execute(expected, () => engine.CycleInvitation(guestOrigin));

    /// <summary>Unlocks the expected external-lock revision.</summary>
    public ControlAuthoritySnapshot Unlock(AuthorityPrecondition expected, long lockRevision) =>
        Execute(expected, () => engine.Unlock(lockRevision));

    /// <summary>Processes deadlines, feedback freshness, and queued device work under the authority gate.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            engine.Tick();
            _snapshotRevision++;
        }
    }

    /// <summary>Stops authority during host shutdown without a caller-provided precondition.</summary>
    public void StopForShutdown()
    {
        lock (_gate)
        {
            engine.Pause("control-service-stopping");
            _authorityRevision++;
            _snapshotRevision++;
        }
    }

    private ControlAuthoritySnapshot Execute(AuthorityPrecondition expected, Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            Validate(expected, engine.Snapshot);
            operation();
            _authorityRevision++;
            _snapshotRevision++;
            return CreateSnapshot();
        }
    }

    private ControlAuthoritySnapshot CreateSnapshot()
    {
        var snapshot = engine.Snapshot;
        return new(snapshot, new(_serviceInstanceId, _authorityRevision,
            new(snapshot.SessionId, snapshot.ConnectionId, snapshot.SessionMode)), _snapshotRevision);
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
}
