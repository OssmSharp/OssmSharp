namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Represents browser-reported guest values and feedback for display, not motion authorization.
/// </summary>
/// <param name="Selected">The guest's currently selected settings.</param>
/// <param name="Sent">The last settings sent to the service, if any.</param>
/// <param name="Confirmed">Whether the browser has confirmed the sent settings.</param>
/// <param name="Status">The browser-side status text.</param>
/// <param name="Error">The browser-side error text, if any.</param>
public sealed record GuestDisplayState(
    ControlValues Selected, ControlValues? Sent, bool Confirmed, string Status, string? Error);
