namespace OSSMDotNet.ControlService;

internal sealed record HubGuestSession(string LogicalConnectionId, string SessionId, string Proof);
