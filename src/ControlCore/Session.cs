namespace OSSMDotNet.Web.ControlCore;

internal sealed class Session(string id, long startedAt, string connectionId, byte[] guestHash)
{
    public string Id { get; } = id;
    public string Mode { get; set; } = "PendingApproval";
    public long StartedAt { get; set; } = startedAt;
    public string? ConnectionId { get; set; } = connectionId;
    public byte[]? GuestHash { get; set; } = guestHash;
}
