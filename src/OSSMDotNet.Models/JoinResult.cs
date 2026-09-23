namespace OSSMDotNet.Models;

/// <summary>Contains the connection-bound proof created when a guest joins an invitation.</summary>
/// <summary>
/// Carries the admitted guest session, connection, and private guest capability.
/// </summary>
/// <param name="SessionId">The session the guest joined.</param>
/// <param name="ConnectionId">The logical connection assigned to the guest.</param>
/// <param name="GuestToken">The private capability used to authorize guest input.</param>
public sealed record JoinResult(string SessionId, string ConnectionId, string GuestToken);
