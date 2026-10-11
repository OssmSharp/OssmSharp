namespace OssmSharp.Web.Contracts;

/// <summary>
/// Describes UI session state that may be disclosed to the guest.
/// </summary>
/// <param name="Joined">Whether this connection is the current guest.</param>
/// <param name="ControlAllowed">Whether the user currently permits settings requests.</param>
/// <param name="ControlExpiresAt">Reserved for compatibility; permission no longer expires.</param>
/// <param name="Device">The runtime's last known device state, if available.</param>
/// <param name="CanResumeAfterStop">Whether a deliberate request may re-arm after the guest's own Stop.</param>
public sealed record GuestSessionState(
    bool Joined,
    bool ControlAllowed,
    DateTimeOffset? ControlExpiresAt,
    DeviceStateSnapshot? Device,
    bool CanResumeAfterStop = false)
{
    /// <summary>
    /// Gets preview-only lengths for an admitted guest, or null when unavailable.
    /// </summary>
    public PreviewDimensions? Preview { get; init; }
}
