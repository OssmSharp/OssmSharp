namespace OSSMDotNet.Models;

/// <summary>Represents one authenticated guest operation sent to the admin service.</summary>
public sealed record GuestCall(int V, string RequestId, string Op, string SessionId, string Proof, string ConnectionId, GuestInput? Input);