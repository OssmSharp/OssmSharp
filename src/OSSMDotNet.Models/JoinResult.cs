namespace OSSMDotNet.Models;

/// <summary>Contains the connection-bound proof created when a guest joins an invitation.</summary>
public sealed record JoinResult(string SessionId, string ConnectionId, string GuestToken);
