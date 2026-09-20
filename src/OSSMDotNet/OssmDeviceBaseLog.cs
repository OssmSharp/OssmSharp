namespace OSSMDotNet;

internal static partial class OssmDeviceBaseLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "A device-change subscriber failed.")]
    public static partial void SubscriberFailed(ILogger logger, Exception exception);
}