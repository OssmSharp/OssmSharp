namespace OssmSharp.Web.GuestWeb.Services;

internal static partial class Log
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Saved the invitation-bound guest browser session with a fixed expiry.")]
    internal static partial void BrowserSessionSaved(this ILogger logger);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Ended the guest browser session and completed required Stop cleanup.")]
    internal static partial void BrowserSessionEnded(this ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Debug, Message = "Guest session {Operation} rejected ({Code}).")]
    internal static partial void SessionRequestRejected(this ILogger logger, string operation, string code);

    [LoggerMessage(EventId = 103, Level = LogLevel.Debug, Message = "Guest session {Operation} did not receive a usable API response.")]
    internal static partial void SessionTransportUnavailable(this ILogger logger, string operation);

    [LoggerMessage(EventId = 104, Level = LogLevel.Error, Message = "Guest session {Operation} received an unusable API result; no usable session result is available.")]
    internal static partial void SessionResponseInvalid(this ILogger logger, string operation);

    [LoggerMessage(EventId = 105, Level = LogLevel.Debug, Message = "Cleared the guest browser cookie without issuing a device request.")]
    internal static partial void BrowserCookieCleared(this ILogger logger);
}
