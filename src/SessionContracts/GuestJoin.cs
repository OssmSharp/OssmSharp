namespace OSSMDotNet.Web.SessionContracts;

public sealed record GuestJoin(string SessionId, string ConnectionId, string GuestToken);
