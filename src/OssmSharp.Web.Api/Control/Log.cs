namespace OssmSharp.Web.Api.Control;

internal static partial class Log
{
    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Guest session changed: {State}.")]
    internal static partial void GuestSessionChanged(this ILogger<ControlSession> logger, string state);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Guest-session Stop cleanup did not complete ({Code}).")]
    internal static partial void GuestStopCleanupIncomplete(this ILogger<ControlSession> logger, string code, Exception exception);

    [LoggerMessage(EventId = 5, Message = "Control operation {Operation} failed ({Code}); {CommandsWritten} commands written. Device connected: {DeviceConnected}, armed: {DeviceArmed}; Stop required: {StopRequired}, resetting position: {ResettingPosition}. Firmware rejection: {FirmwareCode}, state: {FirmwareState}.")]
    internal static partial void DeviceOperationFailed(this ILogger<ControlSession> logger, LogLevel level, string operation, string code, int commandsWritten, bool deviceConnected, bool deviceArmed, bool stopRequired, bool resettingPosition, string? firmwareCode, string? firmwareState, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Control operation {Operation} was cancelled.")]
    internal static partial void ControlOperationCancelled(this ILogger<ControlSession> logger, string operation);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Guest settings requested: pattern {Pattern}, speed {Speed}%, stroke {Stroke}%, depth {Depth}%, sensation {Sensation}%.")]
    internal static partial void SettingsRequested(this ILogger<ControlSession> logger, int pattern, int speed, int stroke, int depth, int sensation);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Guest settings rejected ({Code}). Device connected: {DeviceConnected}, armed: {DeviceArmed}; Stop required: {StopRequired}, resetting position: {ResettingPosition}.")]
    internal static partial void SettingsRejected(this ILogger<ControlSession> logger, string code, bool deviceConnected, bool deviceArmed, bool stopRequired, bool resettingPosition);
}
