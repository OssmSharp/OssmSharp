namespace OSSMDotNet.Models;

/// <summary>Identifies the guest admission state that an operator action expects to affect.</summary>
/// <summary>
/// Identifies the admitted guest and session state used by an authority precondition.
/// </summary>
/// <param name="SessionId">The active session, if one exists.</param>
/// <param name="ConnectionId">The admitted guest connection, if any.</param>
/// <param name="SessionMode">The observed session state.</param>
public sealed record GuestAdmission(string? SessionId, string? ConnectionId, string SessionMode);
