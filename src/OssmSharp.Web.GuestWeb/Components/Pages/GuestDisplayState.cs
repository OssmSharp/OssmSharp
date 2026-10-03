namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Represents browser-reported guest values and feedback for display, not motion authorization.
/// </summary>
/// <param name="Selected">The guest's selected settings, with stroke-relative speed.</param>
/// <param name="Sent">The last acknowledged selection, with stroke-relative speed, if any.</param>
/// <param name="Status">The browser-side status text.</param>
/// <param name="Error">The browser-side error text, if any.</param>
public sealed record GuestDisplayState(
    BrowserSettings Selected, BrowserSettings? Sent, string Status, string? Error);
