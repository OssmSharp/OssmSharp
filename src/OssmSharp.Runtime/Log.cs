using Microsoft.Extensions.Logging;

namespace OssmSharp.Runtime;

internal static partial class Log
{
    [LoggerMessage(2, LogLevel.Error, "Device operation {Operation} failed; commands remain disarmed.")]
    public static partial void OperationFailed(this ILogger logger, string operation, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "A device state subscriber failed.")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(11, LogLevel.Debug, "Device operation {Operation} started.")]
    public static partial void OperationStarted(this ILogger logger, string operation);

    [LoggerMessage(12, LogLevel.Debug, "Device operation {Operation} completed.")]
    public static partial void OperationCompleted(this ILogger logger, string operation);

    [LoggerMessage(13, LogLevel.Information, "Device connection setup was cancelled; awaiting cleanup.")]
    public static partial void ConnectionCancelled(this ILogger logger);

    [LoggerMessage(17, LogLevel.Information, "Device API {Operation} called.")]
    public static partial void ApiCalled(this ILogger logger, string operation);

    [LoggerMessage(18, LogLevel.Information, "Device connection state: {ConnectionState}.")]
    public static partial void ConnectionStateChanged(this ILogger logger, string connectionState);

    [LoggerMessage(19, LogLevel.Information, "Device Set finished with status {Status}; {CommandsWritten} commands written.")]
    public static partial void SettingsFinished(this ILogger logger, SetStatus status, int commandsWritten);

    [LoggerMessage(20, LogLevel.Information, "Device Stop command completed; commands remain disarmed.")]
    public static partial void StopCompleted(this ILogger logger);
}
