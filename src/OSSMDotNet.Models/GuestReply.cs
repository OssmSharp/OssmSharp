namespace OSSMDotNet.Models;

/// <summary>Represents the admin response to one guest-pipe operation.</summary>
public sealed record GuestReply(int V, string RequestId, bool Ok, string Code, GuestStatus? State, GuestJoin? Join);