namespace OssmSharp.Web.GuestWeb.Components.Pages;

internal static partial class Log
{
    [LoggerMessage(EventId = 3, EventName = "LogGuestBrowserConnectionLost", Level = LogLevel.Warning, Message = "Guest browser call {Method} failed; closing control.")]
    internal static partial void BrowserCallFailed(this ILogger<Join> logger, string method, Exception exception);
}
