using Microsoft.Extensions.Logging;

namespace OssmSharp.Runtime.Oem;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Warning, "Device commands disarmed after an unexplained control change.")]
    public static partial void ExternalChange(this ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "Device operation {Operation} failed; commands remain disarmed.")]
    public static partial void OperationFailed(this ILogger logger, string operation, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "A device state subscriber failed.")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);
}
