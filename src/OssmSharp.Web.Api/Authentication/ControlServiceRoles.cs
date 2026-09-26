namespace OssmSharp.Web.Api.Authentication;

/// <summary>
/// Defines trusted service identity roles for the API boundary.
/// </summary>
public static class ControlServiceRoles
{
    /// <summary>
    /// Gets the role for the private administrative web host.
    /// </summary>
    public const string AdminWeb = "AdminWeb";

    /// <summary>
    /// Gets the role for the restricted guest web host.
    /// </summary>
    public const string GuestWeb = "GuestWeb";
}
