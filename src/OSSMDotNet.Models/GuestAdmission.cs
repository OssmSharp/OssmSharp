namespace OSSMDotNet.Models;

/// <summary>Identifies the guest admission state that an operator action expects to affect.</summary>
public sealed record GuestAdmission(string? SessionId, string? ConnectionId, string SessionMode);
