namespace OssmSharp.Web.Api.Authentication;

/// <summary>
/// Defines roles for administrators and guests authenticated with an invitation.
/// </summary>
public static class ControlServiceRoles
{
    /// <summary>
    /// Gets the role for the private administrative web host.
    /// </summary>
    public const string AdminWeb = "AdminWeb";

    /// <summary>
    /// Gets the role for guests authenticated with an invitation.
    /// </summary>
    public const string GuestWeb = "Guest";
}
