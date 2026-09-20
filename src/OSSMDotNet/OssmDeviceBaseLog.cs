namespace OSSMDotNet;

internal static partial class OssmDeviceBaseLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "A device-change subscriber failed.")]
    public static partial void SubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Device connection changed from {PreviousState} to {State}.")]
    public static partial void ConnectionChanged(
        ILogger logger,
        DeviceConnectionState previousState,
        DeviceConnectionState state);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Device connection failed.")]
    public static partial void ConnectionFailed(ILogger logger, Exception exception);
}