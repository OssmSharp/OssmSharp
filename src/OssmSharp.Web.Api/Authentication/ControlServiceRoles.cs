namespace OssmSharp.Web.Api.Authentication;

/// <summary>
/// Defines roles for administrators, invitation redemption, and saved guest sessions.
/// </summary>
public static class ControlServiceRoles
{
    /// <summary>
    /// Gets the role for the private administrative web host.
    /// </summary>
    public const string AdminWeb = "AdminWeb";

    /// <summary>
    /// Gets the role for saved, invitation-bound guest sessions.
    /// </summary>
    public const string GuestWeb = "Guest";

    /// <summary>
    /// Gets the role permitted only to redeem an unconsumed invitation.
    /// </summary>
    public const string Invitation = "Invitation";
}
