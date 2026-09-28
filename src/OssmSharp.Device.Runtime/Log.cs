namespace OssmSharp.Device.Runtime;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "A device-change subscriber failed.")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Device connection changed from {PreviousState} to {State}.")]
    public static partial void ConnectionChanged(
        this ILogger logger,
        DeviceConnectionState previousState,
        DeviceConnectionState state);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Device connection failed.")]
    public static partial void ConnectionFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "The device emergency stop failed; its physical state is unconfirmed.")]
    public static partial void EmergencyStopFailed(this ILogger logger, Exception exception);
}
