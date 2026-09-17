namespace OSSMDotNet.Models;

/// <summary>Identifies the pending guest connection that an administrator approves.</summary>
public sealed record ApproveGuestRequest(string ConnectionId);
