using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Executes guest operations using server-retained connection-scoped admission proofs.</summary>
public sealed class GuestControlOperations(ControlAuthority authority, ControlHubConnectionRegistry registry)
{
    public GuestJoinReceipt Join(string hubConnectionId, GuestJoinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Correlation);
        if (!ControlProtocol.ValidText(request.SessionId, 64) || !ControlProtocol.ValidText(request.InvitationProof, 128))
            throw new ControlRejectedException("invalid-invitation");
        if (!registry.IsGuestStaged(hubConnectionId))
            throw new ControlRejectedException("guest-subscription-required");
        if (registry.TryGetGuest(hubConnectionId, out _))
            throw new ControlRejectedException("guest-already-joined");
        var logicalConnectionId = Guid.NewGuid().ToString("N");
        var joined = authority.JoinGuest(request.SessionId, request.InvitationProof, logicalConnectionId);
        if (!registry.TryBindGuest(hubConnectionId, new(logicalConnectionId, joined.SessionId, joined.GuestToken)))
        {
            authority.GuestTransportLost(logicalConnectionId);
            throw new ControlRejectedException("guest-bind-failed");
        }
        return new("joined", request.Correlation);
    }

    public GuestStatus Status(string hubConnectionId)
    {
        var guest = RequireGuest(hubConnectionId);
        var snapshot = authority.ReadGuest(guest.SessionId, guest.Proof, guest.LogicalConnectionId);
        return ToGuest(snapshot.Snapshot, snapshot.SnapshotRevision);
    }

    public ControlAcknowledgement Submit(string hubConnectionId, GuestInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Correlation);
        var guest = RequireGuest(hubConnectionId);
        var input = request.Input;
        if (!IsValidInput(input))
            throw new ControlRejectedException("invalid-input");
        authority.SubmitGuest(guest.SessionId, guest.Proof, guest.LogicalConnectionId,
            new(input.V, input.Epoch, input.PolicyVersion, input.GrantId, input.Seq, input.Run,
                new(input.Desired.Pattern, input.Desired.Speed, input.Desired.Stroke, input.Desired.Depth, input.Desired.Sensation)));
        return new("accepted", request.Correlation);
    }

    public ControlAcknowledgement Stop(string hubConnectionId, string correlation)
    {
        Validate(correlation);
        var guest = RequireGuest(hubConnectionId);
        authority.StopGuest(guest.SessionId, guest.Proof, guest.LogicalConnectionId);
        return new("stopped", correlation);
    }

    public ControlAcknowledgement Leave(string hubConnectionId, string correlation)
    {
        Validate(correlation);
        var guest = RequireGuest(hubConnectionId);
        authority.LeaveGuest(guest.SessionId, guest.Proof, guest.LogicalConnectionId);
        registry.Remove(hubConnectionId);
        return new("ended", correlation);
    }

    public void TransportLost(string hubConnectionId)
    {
        if (registry.Remove(hubConnectionId) is { } guest)
            authority.GuestTransportLost(guest.LogicalConnectionId);
    }

    private HubGuestSession RequireGuest(string connectionId) =>
        registry.TryGetGuest(connectionId, out var guest) && guest is not null ? guest : throw new ControlRejectedException("guest-not-joined");

    private static void Validate(string? value)
    {
        if (!ControlProtocol.ValidText(value, 64))
            throw new ControlRejectedException("invalid-correlation");
    }

    private static bool IsValidInput(GuestInput? input) =>
        input is { V: 2, Run: true, Desired: not null, PolicyVersion: >= 1, Seq: >= 1 and <= 9_007_199_254_740_991 } &&
        ControlProtocol.ValidText(input.Epoch, 64) && ControlProtocol.ValidText(input.GrantId, 64);

    private static GuestStatus ToGuest(ControlSnapshot state, long snapshotRevision) => new(state.Mode, state.Reason, state.Generation, state.LockRevision,
        state.BridgeReady, state.DeviceMoving, new(state.Desired.Pattern, state.Desired.Speed, state.Desired.Stroke, state.Desired.Depth, state.Desired.Sensation),
        state.Position, state.Velocity, new(state.Limits.SpeedCap, state.Limits.MaximumDepth), state.PolicyVersion, state.SessionMode,
        null, state.AckSeq, state.Grant is { } grant ? new(grant.Id, grant.Epoch, grant.PolicyVersion, grant.ValidForMs) : null)
    {
        Current = state.Current is { } current ? new(current.Pattern, current.Speed, current.Stroke, current.Depth, current.Sensation) : null,
        VerifiedSeq = state.VerifiedSeq,
        DeviceEnabled = state.DeviceEnabled,
        IsSimulator = state.IsSimulator,
        SnapshotRevision = snapshotRevision
    };
}
