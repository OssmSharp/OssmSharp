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

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Guest Send rejected by page readiness. Joined: {Joined}, control allowed: {ControlAllowed}; device connected: {DeviceConnected}, connecting: {DeviceConnecting}, connection failed: {DeviceConnectionFailed}, armed: {DeviceArmed}, resetting position: {ResettingPosition}; resume after Stop: {CanResumeAfterStop}; leaving: {Leaving}, connection lost: {ConnectionLost}, disposed: {Disposed}.")]
    internal static partial void SendNotReady(this ILogger<Join> logger, bool joined, bool controlAllowed, bool deviceConnected, bool deviceConnecting, bool deviceConnectionFailed, bool deviceArmed, bool resettingPosition, bool canResumeAfterStop, bool leaving, bool connectionLost, bool disposed);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Guest Send rejected because settings are outside the supported input ranges.")]
    internal static partial void SendInvalid(this ILogger<Join> logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Guest Send started: pattern {Pattern}, speed {Speed}%, stroke {Stroke}%, depth {Depth}%, sensation {Sensation}%.")]
    internal static partial void SendStarted(this ILogger<Join> logger, int pattern, int speed, int stroke, int depth, int sensation);

    [LoggerMessage(EventId = 9, Message = "Guest Send finished with {Status} ({Code}); {CommandsWritten} commands written.")]
    internal static partial void SendFinished(this ILogger<Join> logger, LogLevel level, string status, string? code, int commandsWritten);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Guest Send did not receive a completed result ({Code}).")]
    internal static partial void SendFailed(this ILogger<Join> logger, string code, Exception exception);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Guest Send response wait was cancelled because the page is closing; the device result is unknown.")]
    internal static partial void SendCancelled(this ILogger<Join> logger);
}
