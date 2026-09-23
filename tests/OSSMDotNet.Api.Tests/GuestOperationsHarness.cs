namespace OSSMDotNet.Api.Tests;

/// <summary>
/// Exercises guest operations with the same staged and bound connection checks as the hub.
/// </summary>
/// <param name="operations">The guest operations under test.</param>
/// <param name="registry">The registry that tracks staged and joined connections.</param>
internal sealed class GuestOperationsHarness(
    GuestControlOperations operations,
    ControlHubConnectionRegistry registry)
{
    internal GuestJoinReceipt Join(string connectionId, GuestJoinRequest request)
    {
        if (!registry.IsGuestStaged(connectionId))
            throw new ControlRejectedException("guest-subscription-required");
        if (registry.TryGetGuest(connectionId, out _))
            throw new ControlRejectedException("guest-already-joined");
        var logicalConnectionId = Guid.NewGuid().ToString("N");
        var joined = operations.Join(logicalConnectionId, request);
        if (!registry.TryBindGuest(connectionId,
            new(logicalConnectionId, joined.SessionId, joined.GuestToken)))
        {
            operations.TransportLost(logicalConnectionId);
            throw new ControlRejectedException("guest-bind-failed");
        }
        return new("joined", request.Correlation);
    }

    internal GuestStatus Status(string connectionId)
    {
        var guest = RequireGuest(connectionId);
        return operations.Status(guest.SessionId, guest.Proof, guest.LogicalConnectionId);
    }

    internal ControlAcknowledgement Submit(string connectionId, GuestInputRequest request)
    {
        var guest = RequireGuest(connectionId);
        return operations.Submit(guest.SessionId, guest.Proof, guest.LogicalConnectionId, request);
    }

    internal ControlAcknowledgement Stop(string connectionId, string correlation)
    {
        var guest = RequireGuest(connectionId);
        return operations.Stop(guest.SessionId, guest.Proof, guest.LogicalConnectionId, correlation);
    }

    internal ControlAcknowledgement Leave(string connectionId, string correlation)
    {
        var guest = RequireGuest(connectionId);
        var result = operations.Leave(guest.SessionId, guest.Proof, guest.LogicalConnectionId, correlation);
        registry.Remove(connectionId);
        return result;
    }

    internal void TransportLost(string connectionId)
    {
        if (registry.Remove(connectionId) is { } guest)
            operations.TransportLost(guest.LogicalConnectionId);
    }

    private HubGuestSession RequireGuest(string connectionId) =>
        registry.TryGetGuest(connectionId, out var guest) && guest is not null
            ? guest
            : throw new ControlRejectedException("guest-not-joined");
}
