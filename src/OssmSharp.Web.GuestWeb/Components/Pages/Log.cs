namespace OssmSharp.Web.GuestWeb.Components.Pages;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, EventName = "LogGuestControlInitializationFailed", Level = LogLevel.Warning, Message = "Guest control initialization failed; closing the connection.")]
    internal static partial void InitializationFailed(this ILogger<Join> logger);

    [LoggerMessage(EventId = 2, EventName = "LogGuestLeaveRejected", Level = LogLevel.Warning, Message = "Guest leave was not accepted: {Code}.")]
    internal static partial void LeaveRejected(this ILogger<Join> logger, string code);

    [LoggerMessage(EventId = 3, EventName = "LogGuestBrowserConnectionLost", Level = LogLevel.Warning, Message = "Guest browser call {Method} failed; closing control.")]
    internal static partial void BrowserCallFailed(this ILogger<Join> logger, string method, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Guest API state could not be read; closing the old controller before browser recovery.")]
    internal static partial void ApiConnectionLost(this ILogger<Join> logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Guest session {Operation} received an unusable response; automatic recovery ended.")]
    internal static partial void SessionReadFailed(this ILogger<Join> logger, string operation);
}
