namespace OSSMDotNet.Api.Authentication;

/// <summary>
/// Defines authorization policy names for API operations.
/// </summary>
public static class ControlServicePolicies
{
    /// <summary>
    /// Gets the policy required for administrator-only operations.
    /// </summary>
    public const string AdminWeb = "ControlAdminWeb";

    /// <summary>
    /// Gets the policy required for guest-service operations.
    /// </summary>
    public const string GuestWeb = "ControlGuestWeb";

    /// <summary>
    /// Gets the policy required to establish a trusted service connection.
    /// </summary>
    public const string Service = "ControlService";
}
