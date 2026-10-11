namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Represents browser-selected guest values and feedback for display, not motion authorization.
/// </summary>
/// <param name="Selected">The guest's selected settings, with speed as an integer percentage.</param>
/// <param name="Sent">The last completed selection, cleared by failed or interrupted requests; speed is an integer percentage.</param>
/// <param name="Status">The browser-side status text.</param>
/// <param name="Error">The browser-side error text, if any.</param>
/// <param name="PreviewStopped">Whether Stop, reset, or unavailable control pauses the illustration.</param>
/// <param name="ErrorVariant">The semantic style for feedback: danger, warning, or info.</param>
/// <param name="CanSend">Whether the browser has a changed selection available for manual sending.</param>
public sealed record GuestDisplayState(
    BrowserSettings Selected, BrowserSettings? Sent, string Status, string? Error, bool PreviewStopped = false,
    string ErrorVariant = "danger", bool CanSend = false);
