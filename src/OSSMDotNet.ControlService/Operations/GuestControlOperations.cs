using OSSMDotNet.ControlService.Authority;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Operations;

/// <summary>Executes guest operations using server-retained connection-scoped admission proofs.</summary>
/// <param name="authority">The owner of guest admission and control state.</param>
public sealed class GuestControlOperations(ControlAuthority authority)
{
    /// <summary>
    /// Redeems an invitation for the authenticated logical connection.
    /// </summary>
    /// <param name="logicalConnectionId">The server-owned connection binding.</param>
    /// <param name="request">The invitation proof and response correlation.</param>
    /// <returns>The admitted session and guest proof.</returns>
    /// <exception cref="ControlRejectedException">The invitation or correlation is invalid.</exception>
    public JoinResult Join(string logicalConnectionId, GuestJoinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Correlation);
        if (!ControlProtocol.ValidText(request.SessionId, 64) || !ControlProtocol.ValidText(request.InvitationProof, 128))
            throw new ControlRejectedException("invalid-invitation");
        return authority.JoinGuest(request.SessionId, request.InvitationProof, logicalConnectionId);
    }

    /// <summary>
    /// Reads status scoped to the authenticated guest binding.
    /// </summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="proof">The server-retained guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned connection binding.</param>
    /// <returns>The guest-safe status without operator-only state.</returns>
    public GuestStatus Status(string sessionId, string proof, string logicalConnectionId)
    {
        var snapshot = authority.ReadGuest(sessionId, proof, logicalConnectionId);
        return ToGuest(snapshot.Snapshot, snapshot.SnapshotRevision);
    }

    /// <summary>
    /// Validates and submits one grant-bound guest motion input.
    /// </summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="proof">The server-retained guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned connection binding.</param>
    /// <param name="request">The correlated, sequenced motion input.</param>
    /// <returns>An acknowledgement when the input is accepted.</returns>
    /// <exception cref="ControlRejectedException">The input or guest authority is invalid.</exception>
    public ControlAcknowledgement Submit(
        string sessionId,
        string proof,
        string logicalConnectionId,
        GuestInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ControlProtocol.ValidText(request.Correlation, 64))
        {
            authority.RecordRejectedGuestInputAttempt(sessionId, proof, logicalConnectionId);
            throw new ControlRejectedException("invalid-correlation");
        }

        var input = request.Input;
        if (!IsValidInput(input))
        {
            authority.RecordRejectedGuestInputAttempt(sessionId, proof, logicalConnectionId);
            throw new ControlRejectedException("invalid-input");
        }
        authority.SubmitGuest(sessionId, proof, logicalConnectionId,
            new(input.V, input.Epoch, input.PolicyVersion, input.GrantId, input.Seq, input.Run,
                new(
                    MotionDeviceState.StrokeEngineIdle,
                    input.Desired.Speed,
                    input.Desired.Stroke,
                    input.Desired.Sensation,
                    input.Desired.Depth,
                    0,
                    (MotionPattern)input.Desired.Pattern,
                    0)));
        return new("accepted", request.Correlation);
    }

    /// <summary>
    /// Stops motion for the authenticated guest session.
    /// </summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="proof">The server-retained guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned connection binding.</param>
    /// <param name="correlation">The response correlation identifier.</param>
    /// <returns>The correlated stop acknowledgement.</returns>
    public ControlAcknowledgement Stop(string sessionId, string proof, string logicalConnectionId, string correlation)
    {
        Validate(correlation);
        authority.StopGuest(sessionId, proof, logicalConnectionId);
        return new("stopped", correlation);
    }

    /// <summary>
    /// Ends the authenticated guest session without cycling its invitation.
    /// </summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="proof">The server-retained guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned connection binding.</param>
    /// <param name="correlation">The response correlation identifier.</param>
    /// <returns>The correlated leave acknowledgement.</returns>
    public ControlAcknowledgement Leave(string sessionId, string proof, string logicalConnectionId, string correlation)
    {
        Validate(correlation);
        authority.LeaveGuest(sessionId, proof, logicalConnectionId);
        return new("ended", correlation);
    }

    /// <summary>
    /// Revokes the guest binding when its transport disconnects.
    /// </summary>
    /// <param name="logicalConnectionId">The lost server-owned connection binding.</param>
    public void TransportLost(string logicalConnectionId) => authority.GuestTransportLost(logicalConnectionId);

    /// <summary>Records an authenticated transport-level malformed input attempt against its bound admission.</summary>
    /// <param name="sessionId">The admitted session identifier.</param>
    /// <param name="proof">The private guest proof.</param>
    /// <param name="logicalConnectionId">The server-owned guest connection binding.</param>
    public void RecordRejectedSubmit(string sessionId, string proof, string logicalConnectionId) =>
        authority.RecordRejectedGuestInputAttempt(sessionId, proof, logicalConnectionId);

    private static void Validate(string? value)
    {
        if (!ControlProtocol.ValidText(value, 64))
            throw new ControlRejectedException("invalid-correlation");
    }

    private static bool IsValidInput(GuestInput? input) =>
        input is { V: 2, Run: true, Desired: not null, PolicyVersion: >= 1, Seq: >= 1 and <= 9_007_199_254_740_991 } &&
        ControlProtocol.ValidText(input.Epoch, 64) && ControlProtocol.ValidText(input.GrantId, 64);

    private static GuestStatus ToGuest(ControlSnapshot state, long snapshotRevision) => new(state.Mode, state.Reason, state.Generation, state.LockRevision,
        state.BridgeReady, state.DeviceMoving, new((int)state.Desired.Pattern, state.Desired.Speed, state.Desired.Stroke, state.Desired.Depth, state.Desired.Sensation),
        state.Position, state.Velocity, new(state.Limits.SpeedCap, state.Limits.MaximumDepth), state.PolicyVersion, state.SessionMode,
        null, state.AckSeq, state.Grant is { } grant ? new(grant.Id, grant.Epoch, grant.PolicyVersion, grant.ValidForMs) : null)
    {
        Current = state.Current is { } current ? new((int)current.Pattern, current.Speed, current.Stroke, current.Depth, current.Sensation) : null,
        VerifiedSeq = state.VerifiedSeq,
        DeviceEnabled = state.DeviceEnabled,
        IsSimulator = state.IsSimulator,
        SnapshotRevision = snapshotRevision
    };
}
