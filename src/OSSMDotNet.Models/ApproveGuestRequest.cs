namespace OSSMDotNet.Models;

/// <summary>Identifies the pending guest connection that an administrator approves.</summary>
/// <summary>
/// Identifies the waiting guest connection to approve.
/// </summary>
/// <param name="ConnectionId">The pending guest connection to approve.</param>
public sealed record ApproveGuestRequest(string ConnectionId);
