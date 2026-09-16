namespace OSSMDotNet.Models;

/// <summary>Contains the guest session proof established by a successful join.</summary>
public sealed record GuestJoin(string SessionId, string ConnectionId, string GuestToken);