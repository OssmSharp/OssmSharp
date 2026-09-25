using Microsoft.Extensions.Logging;

namespace OSSMDotNet.ControlClient.Connections;

internal static partial class Log
{
    [LoggerMessage(EventId = 1900, Level = LogLevel.Warning, Message = "Control-service connection closed unexpectedly ({Reason}); client state requires resynchronization.")]
    internal static partial void ConnectionLost(this ILogger logger, string reason);

    [LoggerMessage(EventId = 1901, Level = LogLevel.Warning, Message = "Control command {Method} acknowledgement timed out; its outcome is unknown.")]
    internal static partial void CommandOutcomeUnknown(this ILogger logger, string method);
}
