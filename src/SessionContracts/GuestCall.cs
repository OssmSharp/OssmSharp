namespace OSSMDotNet.Web.SessionContracts;

public sealed record GuestCall(int V, string RequestId, string Op, string SessionId, string Proof, string ConnectionId, GuestInput? Input);
