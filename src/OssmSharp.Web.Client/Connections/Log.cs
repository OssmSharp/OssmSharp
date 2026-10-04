using Microsoft.Extensions.Logging;

namespace OssmSharp.Web.Client.Connections;

internal static partial class Log
{
    [LoggerMessage(EventId = 1900, Level = LogLevel.Warning, Message = "Control-service connection closed unexpectedly ({Reason}); client state requires resynchronization.")]
    internal static partial void ConnectionLost(this ILogger logger, string reason);

    [LoggerMessage(EventId = 1901, Level = LogLevel.Warning, Message = "RPC request {Method} acknowledgement timed out; its result is unknown.")]
    internal static partial void RequestOutcomeUnknown(this ILogger logger, string method);
}
