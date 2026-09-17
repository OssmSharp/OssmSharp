namespace OSSMDotNet.Models;

/// <summary>Contains the invitation proof submitted to establish a hub-scoped guest admission.</summary>
public sealed record GuestJoinRequest(string Correlation, string SessionId, string InvitationProof);
