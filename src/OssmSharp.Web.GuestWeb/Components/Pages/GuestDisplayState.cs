namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Represents browser-reported guest values and feedback for display, not motion authorization.
/// </summary>
/// <param name="Selected">The guest's selected settings, with speed as an integer percentage.</param>
/// <param name="Sent">The last acknowledged selection, with speed as an integer percentage, if any.</param>
/// <param name="Status">The browser-side status text.</param>
/// <param name="Error">The browser-side error text, if any.</param>
/// <param name="PreviewStopped">Whether motion preview is paused until a deliberate settings request succeeds.</param>
public sealed record GuestDisplayState(
    BrowserSettings Selected, BrowserSettings? Sent, string Status, string? Error, bool PreviewStopped = false);
