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
}
