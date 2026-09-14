namespace OSSMDotNet.Web.ControlCore;

internal sealed class Session(Guid attemptId, string id, long startedAt, byte[] invitationHash, Invitation result)
{
    public Guid AttemptId { get; } = attemptId;
    public string Id { get; } = id;
    public string Mode { get; set; } = "Invited";
    public long StartedAt { get; set; } = startedAt;
    public string? ConnectionId { get; set; }
    public byte[]? InvitationHash { get; set; } = invitationHash;
    public byte[]? GuestHash { get; set; }
    public Invitation? CreationResult { get; set; } = result;
}
