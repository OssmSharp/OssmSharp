namespace OssmSharp.Web.Contracts;

/// <summary>
/// Describes single-use invitation redemption without attaching a controller or granting motion permission.
/// </summary>
/// <param name="Correlation">The request identifier.</param>
/// <param name="Code">The accepted or rejected result code.</param>
/// <param name="Accepted">Whether the invitation was consumed.</param>
/// <param name="SessionToken">The private saved-session credential, present only on acceptance. Never expose it to browser JavaScript or logs.</param>
/// <param name="ExpiresAt">The fixed expiry, 24 hours after redemption, present only on acceptance.</param>
public sealed record GuestRedemptionResponse(
    string Correlation,
    string Code,
    bool Accepted,
    string? SessionToken = null,
    DateTimeOffset? ExpiresAt = null);
