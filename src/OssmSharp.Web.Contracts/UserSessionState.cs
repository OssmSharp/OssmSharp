namespace OssmSharp.Web.Contracts;

/// <summary>
/// Describes invitation, guest permission, and device state visible to the user.
/// </summary>
/// <param name="Invitation">The invitation offered to a guest.</param>
/// <param name="GuestJoined">Whether a guest is currently admitted.</param>
/// <param name="GuestControlAllowed">Whether the user currently permits guest settings requests.</param>
/// <param name="ControlExpiresAt">Reserved for compatibility; guest permission no longer expires.</param>
/// <param name="Device">The runtime's last known device state, if available.</param>
public sealed record UserSessionState(
    GuestInvitation Invitation,
    bool GuestJoined,
    bool GuestControlAllowed,
    DateTimeOffset? ControlExpiresAt,
    DeviceStateSnapshot? Device)
{
    /// <summary>
    /// Gets the shared preview-only lengths, or null when unavailable from an older service.
    /// </summary>
    public PreviewDimensions? Preview { get; init; }

    /// <summary>
    /// Gets whether the admitted guest currently has an attached controller.
    /// Admission and permission can persist while the browser is offline.
    /// </summary>
    public bool GuestConnected { get; init; }

    /// <summary>
    /// Gets the absolute saved-session expiry, or null when no guest is admitted.
    /// </summary>
    public DateTimeOffset? GuestSessionExpiresAt { get; init; }

    /// <summary>
    /// Gets whether the current invitation has already been redeemed.
    /// Replace it to admit a different guest; replacement ends the current session.
    /// </summary>
    public bool InvitationConsumed { get; init; }
}
