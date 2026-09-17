namespace OSSMDotNet.ControlService;

/// <summary>Defines trusted service identity roles for the control-service boundary.</summary>
public static class ControlServiceRoles
{
    /// <summary>Gets the role for the private administrative web host.</summary>
    public const string AdminWeb = "AdminWeb";

    /// <summary>Gets the role for the restricted guest web host.</summary>
    public const string GuestWeb = "GuestWeb";
}
