namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Contains complete guest settings for display or settings requests.
/// </summary>
/// <remarks>
/// Selected, sent, and submitted values express speed as an integer percentage
/// in [0, 100], using 50 for 50%. Only the runtime converts them to firmware velocity.
/// Zero speed sends zero velocity. Speed is not physical speed or strokes per minute.
/// </remarks>
public sealed record BrowserSettings(int Pattern, int Speed, int Stroke, int Depth, int Sensation);
