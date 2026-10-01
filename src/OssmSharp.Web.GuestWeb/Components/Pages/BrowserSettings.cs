namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Contains complete guest settings for display or submission.
/// </summary>
/// <remarks>
/// Selected display values use a stroke-relative speed percentage. Submissions and sent
/// values use an integer firmware velocity percentage, rounded down after stroke scaling.
/// </remarks>
public sealed record BrowserSettings(int Pattern, int Speed, int Stroke, int Depth, int Sensation);
