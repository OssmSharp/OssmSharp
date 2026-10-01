namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Contains the complete browser-selected settings before server-side validation.
/// </summary>
public sealed record BrowserSettings(int Pattern, int Speed, int Stroke, int Depth, int Sensation);
