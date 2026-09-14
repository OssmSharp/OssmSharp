namespace OSSMDotNet.Web.SessionContracts;

public sealed record GuestReply(int V, string RequestId, bool Ok, string Code, GuestStatus? State, GuestJoin? Join);
