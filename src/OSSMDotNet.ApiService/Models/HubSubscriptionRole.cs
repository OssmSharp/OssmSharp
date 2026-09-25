namespace OSSMDotNet.ApiService.Models;

/// <summary>
/// Distinguishes operator and guest live-state projections for a hub subscription.
/// </summary>
internal enum HubSubscriptionRole
{
    /// <summary>
    /// Receives trusted operator state and activity.
    /// </summary>
    Operator,
    /// <summary>
    /// Receives only guest-scoped status.
    /// </summary>
    Guest
}
